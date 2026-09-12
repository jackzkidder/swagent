using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Anthropic;
using Anthropic.Exceptions;
using Anthropic.Models.Messages;
using SwAgent.Core.Infrastructure;
using SwAgent.Core.Session;
using SwAgent.Core.Tools;

namespace SwAgent.Agent
{
    /// <summary>Progress the UI can show while the agent works.</summary>
    public sealed class AgentEvent
    {
        public enum Kind { Thinking, Text, ToolCall, ToolResult, Retry, Done, Failed }

        public Kind Type { get; set; }
        public string Message { get; set; }
        public string ToolName { get; set; }
        public bool Ok { get; set; } = true;
    }

    /// <summary>What a completed run produced.</summary>
    public sealed class AgentRunResult
    {
        public bool Completed { get; set; }
        public string FinalText { get; set; }
        public int Turns { get; set; }
        public int ToolCalls { get; set; }
        public string StoppedBecause { get; set; }
        public decimal EstimatedCostUsd { get; set; }
    }

    /// <summary>
    /// The agent loop.
    ///
    /// The threading rule is the whole design. HTTP is awaited on a worker
    /// thread so the CAD UI never freezes mid-conversation, and every tool call
    /// is marshalled back onto the SOLIDWORKS thread through ISwDispatcher,
    /// because COM demands it. Neither half may be skipped: run HTTP on the
    /// SOLIDWORKS thread and the whole application locks up; run COM off it and
    /// the behaviour is undefined.
    ///
    /// This is a hand-written loop rather than the SDK's tool runner
    /// specifically because of that marshalling, and because history needs
    /// pruning as it grows.
    /// </summary>
    public sealed class CadAgent
    {
        private readonly AnthropicClient _client;
        private readonly ISwDispatcher _dispatcher;
        private readonly SwSession _session;
        private readonly ToolRegistry _registry;
        private readonly ISwLog _log;
        private readonly Conversation _conversation = new Conversation();

        /// <summary>
        /// A ceiling on tool calls per user message. A model that has
        /// misunderstood can otherwise loop indefinitely against the user's
        /// own API credit, which is exactly the anxiety BYOK creates.
        /// </summary>
        public int MaxTurns { get; set; } = 40;

        public string Model { get; set; } = ModelIds.Opus5;
        public int MaxTokens { get; set; } = 8000;

        public CostMeter Cost { get; }

        public CadAgent(
            string apiKey,
            ISwDispatcher dispatcher,
            SwSession session,
            ToolRegistry registry,
            ISwLog log = null)
        {
            if (string.IsNullOrWhiteSpace(apiKey))
                throw new ArgumentException("An API key is required.", nameof(apiKey));

            _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
            _session = session ?? throw new ArgumentNullException(nameof(session));
            _registry = registry ?? throw new ArgumentNullException(nameof(registry));
            _log = log ?? NullSwLog.Instance;

            // Always constructed with an explicit key, which takes precedence
            // over the SDK's credential resolution. That matters: if we ever
            // allowed a null key through, the SDK would quietly fall back to
            // ANTHROPIC_API_KEY or an OAuth profile on the machine and bill
            // somebody else's credential instead of prompting for setup. The
            // guard at the top of this constructor is what prevents that.
            _client = new AnthropicClient { ApiKey = apiKey };
            Cost = new CostMeter(ModelPricing.For(Model));
        }

        /// <summary>
        /// Run one user request to completion.
        ///
        /// Never throws for an ordinary failure: a network problem, a bad key
        /// or an exhausted turn limit comes back as a result with
        /// <see cref="AgentRunResult.Completed"/> false, because this is called
        /// from UI code inside a CAD session.
        /// </summary>
        public async Task<AgentRunResult> RunAsync(
            string userMessage,
            IProgress<AgentEvent> progress = null,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            var result = new AgentRunResult();
            _conversation.AddUserText(userMessage);

            var tools = AnthropicToolAdapter.ToSdkTools(_registry);

            try
            {
                for (int turn = 0; turn < MaxTurns; turn++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    result.Turns = turn + 1;

                    Message response = await SendWithRetriesAsync(tools, progress, cancellationToken)
                        .ConfigureAwait(false);

                    if (response == null)
                    {
                        result.StoppedBecause = "The request to Anthropic could not be completed.";
                        progress?.Report(new AgentEvent { Type = AgentEvent.Kind.Failed, Message = result.StoppedBecause, Ok = false });
                        return Finish(result);
                    }

                    RecordUsage(response);

                    // Opus 5 can decline a request outright. Check the stop
                    // reason before reading content, or the refusal reads as an
                    // empty response.
                    if (IsRefusal(response))
                    {
                        result.StoppedBecause =
                            "Anthropic declined this request. Rephrasing it usually helps; " +
                            "if it persists, the request may be outside what the model will do.";
                        progress?.Report(new AgentEvent { Type = AgentEvent.Kind.Failed, Message = result.StoppedBecause, Ok = false });
                        return Finish(result);
                    }

                    var assistantBlocks = new List<ContentBlockParam>();
                    var pendingToolUses = new List<(string Id, string Name, JsonElement Input)>();
                    string turnText = null;

                    foreach (ContentBlock block in response.Content)
                    {
                        if (block.TryPickText(out TextBlock text))
                        {
                            assistantBlocks.Add(new TextBlockParam { Text = text.Text });
                            turnText = text.Text;
                            progress?.Report(new AgentEvent { Type = AgentEvent.Kind.Text, Message = text.Text });
                        }
                        else if (block.TryPickThinking(out ThinkingBlock thinking))
                        {
                            // The signature must be preserved exactly; the API
                            // rejects tampered thinking blocks.
                            assistantBlocks.Add(new ThinkingBlockParam
                            {
                                Thinking = thinking.Thinking,
                                Signature = thinking.Signature,
                            });
                        }
                        else if (block.TryPickRedactedThinking(out RedactedThinkingBlock redacted))
                        {
                            assistantBlocks.Add(new RedactedThinkingBlockParam { Data = redacted.Data });
                        }
                        else if (block.TryPickToolUse(out ToolUseBlock toolUse))
                        {
                            assistantBlocks.Add(new ToolUseBlockParam
                            {
                                ID = toolUse.ID,
                                Name = toolUse.Name,
                                Input = toolUse.Input,
                            });
                            pendingToolUses.Add((toolUse.ID, toolUse.Name, ToJsonElement(toolUse.Input)));
                        }
                    }

                    _conversation.AddAssistant(assistantBlocks);

                    if (pendingToolUses.Count == 0)
                    {
                        // No tools requested: the model is done talking.
                        result.Completed = true;
                        result.FinalText = turnText;
                        progress?.Report(new AgentEvent { Type = AgentEvent.Kind.Done, Message = turnText });
                        return Finish(result);
                    }

                    var toolResults = new List<ToolResultEntry>();

                    foreach (var call in pendingToolUses)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        result.ToolCalls++;

                        progress?.Report(new AgentEvent
                        {
                            Type = AgentEvent.Kind.ToolCall,
                            ToolName = call.Name,
                            Message = call.Name,
                        });

                        ToolResult toolResult = await ExecuteOnSwThreadAsync(call.Name, call.Input, cancellationToken)
                            .ConfigureAwait(false);

                        progress?.Report(new AgentEvent
                        {
                            Type = AgentEvent.Kind.ToolResult,
                            ToolName = call.Name,
                            Message = toolResult.Text,
                            Ok = toolResult.Ok,
                        });

                        byte[] png = toolResult.Images.Count > 0 ? toolResult.Images[0].PngBytes : null;

                        toolResults.Add(new ToolResultEntry(
                            call.Id, toolResult.Text, png, isError: !toolResult.Ok));

                        // A lost session is not retryable. Stop rather than
                        // sending forty more tool calls into a dead pointer.
                        if (toolResult.ErrorKind == "session_lost")
                        {
                            _conversation.AddToolResults(toolResults);
                            result.StoppedBecause = toolResult.Text;
                            progress?.Report(new AgentEvent { Type = AgentEvent.Kind.Failed, Message = toolResult.Text, Ok = false });
                            return Finish(result);
                        }
                    }

                    // All results for one assistant turn go back in a SINGLE
                    // user message. Splitting them teaches the model to stop
                    // requesting tools in parallel.
                    _conversation.AddToolResults(toolResults);
                }

                result.StoppedBecause =
                    $"Stopped after {MaxTurns} tool calls without finishing. The part may be " +
                    "partly built; check the feature tree before continuing.";
                progress?.Report(new AgentEvent { Type = AgentEvent.Kind.Failed, Message = result.StoppedBecause, Ok = false });
                return Finish(result);
            }
            catch (OperationCanceledException)
            {
                result.StoppedBecause = "Cancelled.";
                return Finish(result);
            }
            catch (Exception ex)
            {
                // The loop runs on a worker, but the UI calling it lives inside
                // SOLIDWORKS. Nothing escapes.
                _log.Error($"Agent loop failed: {ex}");
                result.StoppedBecause = $"The agent stopped unexpectedly: {ex.Message}";
                progress?.Report(new AgentEvent { Type = AgentEvent.Kind.Failed, Message = result.StoppedBecause, Ok = false });
                return Finish(result);
            }
        }

        private AgentRunResult Finish(AgentRunResult result)
        {
            result.EstimatedCostUsd = Cost.EstimatedCostUsd;
            _log.Info($"Agent run finished: completed={result.Completed}, turns={result.Turns}, " +
                      $"tools={result.ToolCalls}, {Cost.Describe()}");
            return result;
        }

        /// <summary>
        /// Execute a tool on the SOLIDWORKS thread.
        ///
        /// This is the marshalling half of the threading rule. The registry
        /// already wraps every tool in the exception boundary, so this returns a
        /// ToolResult rather than throwing - except when the dispatcher itself
        /// reports the session is gone.
        /// </summary>
        private async Task<ToolResult> ExecuteOnSwThreadAsync(
            string toolName, JsonElement input, CancellationToken cancellationToken)
        {
            try
            {
                return await _dispatcher.InvokeAsync(
                    () => _registry.Execute(toolName, input, _session, _log),
                    cancellationToken).ConfigureAwait(false);
            }
            catch (SwSessionLostException ex)
            {
                return ToolResult.Fatal(ex.Message);
            }
            catch (Exception ex)
            {
                _log.Error($"Dispatching {toolName} failed: {ex.Message}");
                return ToolResult.Failure($"{toolName} could not be dispatched: {ex.Message}", "dispatch_failed");
            }
        }

        /// <summary>
        /// Send the request, retrying the failures that are worth retrying.
        ///
        /// 429 (rate limited) and 529 (overloaded) are transient and must back
        /// off visibly - a silent hang reads to the user as a frozen add-in.
        /// A bad key or exhausted credit is not retryable and gets its own
        /// message, because "try again later" is useless advice for a problem
        /// only the user can fix.
        /// </summary>
        private async Task<Message> SendWithRetriesAsync(
            List<ToolUnion> tools, IProgress<AgentEvent> progress, CancellationToken cancellationToken)
        {
            const int maxAttempts = 4;

            for (int attempt = 1; attempt <= maxAttempts; attempt++)
            {
                try
                {
                    var parameters = new MessageCreateParams
                    {
                        Model = Model,
                        MaxTokens = MaxTokens,

                        // Cache the stable prefix. Requests render as
                        // tools -> system -> messages, so a breakpoint on the
                        // system block covers the tool schemas as well - and
                        // those are identical on every single turn of a run.
                        //
                        // This is the difference between paying full input rate
                        // for ~3K tokens of schema on every tool call and paying
                        // a tenth of it. Over a twenty-feature part that is most
                        // of the bill.
                        //
                        // Both halves are byte-stable by construction: the
                        // system prompt is a constant, and the tool list is
                        // ordered by name. Neither may gain a timestamp or a
                        // per-request id without silently killing the cache.
                        System = new List<TextBlockParam>
                        {
                            new TextBlockParam
                            {
                                Text = SystemPrompt.Text,
                                CacheControl = new CacheControlEphemeral(),
                            },
                        },

                        Tools = tools,
                        Messages = _conversation.BuildMessages(),
                    };

                    // ConfigureAwait(false): this must not resume on the
                    // SOLIDWORKS thread. The whole point is that the network
                    // wait happens somewhere else.
                    return await _client.Messages.Create(parameters, cancellationToken: cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (AnthropicUnauthorizedException)
                {
                    progress?.Report(new AgentEvent
                    {
                        Type = AgentEvent.Kind.Failed,
                        Ok = false,
                        Message = "Your API key was rejected. Check it in SwAgent settings and re-enter it.",
                    });
                    return null;
                }
                catch (AnthropicRateLimitException ex)
                {
                    if (attempt == maxAttempts) { ReportGaveUp(progress, "rate limited"); return null; }
                    await BackOffAsync(attempt, "Rate limited", progress, cancellationToken).ConfigureAwait(false);
                    _log.Debug($"429 on attempt {attempt}: {ex.Message}");
                }
                catch (Anthropic5xxException ex)
                {
                    // 529 (overloaded) arrives here alongside ordinary 5xx.
                    if (attempt == maxAttempts) { ReportGaveUp(progress, "Anthropic is overloaded"); return null; }
                    await BackOffAsync(attempt, "Anthropic is busy", progress, cancellationToken).ConfigureAwait(false);
                    _log.Debug($"5xx on attempt {attempt}: {ex.Message}");
                }
                catch (AnthropicApiException ex)
                {
                    // Includes 400s such as exhausted credit. Not retryable,
                    // and the message is the actionable part.
                    string message = DescribeApiFailure(ex);
                    _log.Error($"API error: {ex.Message}");
                    progress?.Report(new AgentEvent { Type = AgentEvent.Kind.Failed, Message = message, Ok = false });
                    return null;
                }
                catch (Exception ex) when (!(ex is OperationCanceledException))
                {
                    if (attempt == maxAttempts)
                    {
                        _log.Error($"Network failure: {ex.Message}");
                        progress?.Report(new AgentEvent
                        {
                            Type = AgentEvent.Kind.Failed,
                            Ok = false,
                            Message = "Could not reach api.anthropic.com. Check the network connection.",
                        });
                        return null;
                    }

                    await BackOffAsync(attempt, "Connection problem", progress, cancellationToken).ConfigureAwait(false);
                }
            }

            return null;
        }

        private static string DescribeApiFailure(AnthropicApiException ex)
        {
            string raw = ex.Message ?? string.Empty;

            // Insufficient credit is its own actionable state, not a generic
            // API error: the user has to go and top up, and no amount of
            // retrying will help.
            if (raw.IndexOf("credit", StringComparison.OrdinalIgnoreCase) >= 0
                || raw.IndexOf("billing", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return "Your Anthropic account is out of credit. Add credit at console.anthropic.com, " +
                       "then try again.";
            }

            return "Anthropic rejected the request: " + raw;
        }

        private void ReportGaveUp(IProgress<AgentEvent> progress, string why)
        {
            string message = $"Gave up after several retries ({why}). Try again in a moment.";
            _log.Error(message);
            progress?.Report(new AgentEvent { Type = AgentEvent.Kind.Failed, Message = message, Ok = false });
        }

        /// <summary>Exponential backoff, reported so the user sees waiting rather than hanging.</summary>
        private static async Task BackOffAsync(
            int attempt, string reason, IProgress<AgentEvent> progress, CancellationToken cancellationToken)
        {
            int seconds = (int)Math.Pow(2, attempt); // 2, 4, 8
            progress?.Report(new AgentEvent
            {
                Type = AgentEvent.Kind.Retry,
                Message = $"{reason}; retrying in {seconds}s...",
            });

            await Task.Delay(TimeSpan.FromSeconds(seconds), cancellationToken).ConfigureAwait(false);
        }

        private void RecordUsage(Message response)
        {
            try
            {
                var usage = response.Usage;
                if (usage == null) return;

                Cost.Record(
                    usage.InputTokens,
                    usage.OutputTokens,
                    usage.CacheReadInputTokens ?? 0,
                    usage.CacheCreationInputTokens ?? 0);
            }
            catch (Exception ex)
            {
                _log.Debug($"Could not record usage: {ex.Message}");
            }
        }

        private static bool IsRefusal(Message response)
        {
            try
            {
                return response.StopReason != null
                    && response.StopReason.ToString().IndexOf("refusal", StringComparison.OrdinalIgnoreCase) >= 0;
            }
            catch
            {
                return false;
            }
        }

        private static JsonElement ToJsonElement(object input)
        {
            if (input is JsonElement element) return element;

            // Tool inputs must be parsed, never string-matched: escaping in the
            // serialized form varies between models.
            string json = input == null ? "{}" : JsonSerializer.Serialize(input);
            using (var doc = JsonDocument.Parse(json))
                return doc.RootElement.Clone();
        }
    }
}

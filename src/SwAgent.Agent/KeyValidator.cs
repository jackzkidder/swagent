using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Anthropic;
using Anthropic.Exceptions;
using Anthropic.Models.Messages;

namespace SwAgent.Agent
{
    /// <summary>Why a key was not accepted. Each needs different advice.</summary>
    public enum KeyValidationResult
    {
        Valid,

        /// <summary>The key is wrong, revoked, or mistyped.</summary>
        InvalidKey,

        /// <summary>The key is good but the account has no credit.</summary>
        NoCredit,

        /// <summary>We could not reach Anthropic at all.</summary>
        NoNetwork,

        /// <summary>Something else went wrong; the message says what.</summary>
        Unknown
    }

    public sealed class KeyValidation
    {
        public KeyValidationResult Result { get; set; }
        public string Message { get; set; }
        public bool IsValid => Result == KeyValidationResult.Valid;
    }

    /// <summary>
    /// Checks a key with a live call before we accept it.
    ///
    /// The point is to fail at the moment the user pastes the key, not twenty
    /// minutes later in the middle of their first part - and to say which of
    /// three quite different things went wrong. "Something went wrong" sends a
    /// mechanical engineer who has just made an Anthropic account to support;
    /// "your account has no credit, add some at console.anthropic.com" sends
    /// them to the page that fixes it.
    ///
    /// The call is deliberately tiny: a handful of input tokens and a single
    /// output token, costing a small fraction of a cent.
    /// </summary>
    public static class KeyValidator
    {
        public static async Task<KeyValidation> ValidateAsync(
            string apiKey, CancellationToken cancellationToken = default(CancellationToken))
        {
            if (!ApiKeyStore.LooksWellFormed(apiKey))
            {
                return new KeyValidation
                {
                    Result = KeyValidationResult.InvalidKey,
                    Message = "That does not look like an Anthropic API key. They begin with 'sk-ant-'.",
                };
            }

            var client = new AnthropicClient { ApiKey = apiKey.Trim() };

            try
            {
                await client.Messages.Create(new MessageCreateParams
                {
                    Model = ModelIds.Opus5,
                    MaxTokens = 1,
                    Messages = new List<MessageParam>
                    {
                        new MessageParam { Role = Role.User, Content = "Hi" },
                    },
                }, cancellationToken: cancellationToken).ConfigureAwait(false);

                return new KeyValidation
                {
                    Result = KeyValidationResult.Valid,
                    Message = "Key accepted.",
                };
            }
            catch (AnthropicUnauthorizedException)
            {
                return new KeyValidation
                {
                    Result = KeyValidationResult.InvalidKey,
                    Message = "Anthropic rejected this key. Check for a missing character, or create a " +
                              "new key at console.anthropic.com.",
                };
            }
            catch (AnthropicApiException ex)
            {
                string raw = ex.Message ?? string.Empty;

                if (raw.IndexOf("credit", StringComparison.OrdinalIgnoreCase) >= 0
                    || raw.IndexOf("billing", StringComparison.OrdinalIgnoreCase) >= 0
                    || raw.IndexOf("quota", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return new KeyValidation
                    {
                        Result = KeyValidationResult.NoCredit,
                        Message = "This key works, but the account has no credit. Add credit at " +
                                  "console.anthropic.com, then try again.",
                    };
                }

                return new KeyValidation
                {
                    Result = KeyValidationResult.Unknown,
                    Message = "Anthropic returned an error: " + raw,
                };
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Everything left is a transport problem: no DNS, no route, a
                // proxy in the way. Common on a corporate network, and nothing
                // to do with the key.
                return new KeyValidation
                {
                    Result = KeyValidationResult.NoNetwork,
                    Message = "Could not reach api.anthropic.com. Check the network connection, or a " +
                              $"proxy or firewall that might be blocking it. ({ex.GetType().Name})",
                };
            }
        }
    }
}

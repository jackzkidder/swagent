using System;
using System.Collections.Generic;
using System.Linq;
using Anthropic.Models.Messages;

namespace SwAgent.Agent
{
    /// <summary>One tool's result, before it becomes a tool_result block.</summary>
    public sealed class ToolResultEntry
    {
        public ToolResultEntry(string toolUseId, string text, byte[] png, bool isError)
        {
            ToolUseId = toolUseId;
            Text = text;
            Png = png;
            IsError = isError;
        }

        public string ToolUseId { get; }
        public string Text { get; }

        /// <summary>The viewport capture, or null. May be dropped when pruning.</summary>
        public byte[] Png { get; }

        public bool IsError { get; }
        public bool HasImage => Png != null && Png.Length > 0;
    }

    /// <summary>
    /// The message history, and the policy for what stays in it.
    ///
    /// Screenshots are the reason this class exists. A twenty-feature part
    /// produces twenty viewport captures, and carrying them all forward will
    /// exhaust the context window long before the part is finished - while
    /// buying nothing, because a screenshot of feature 3 is irrelevant once
    /// feature 15 exists.
    ///
    /// So old images are pruned and their text is kept. That ordering matters:
    /// the text carries the bounding box and mass, which are the ground truth,
    /// while the image was only ever a sanity check on the operation that
    /// produced it.
    ///
    /// What is never pruned is the tool_result block itself. The API rejects a
    /// request where a tool_use has no matching tool_result, so pruning removes
    /// the image from the result, never the result.
    /// </summary>
    public sealed class Conversation
    {
        private readonly List<Entry> _entries = new List<Entry>();

        /// <summary>
        /// How many of the most recent image-bearing tool results keep their
        /// image. Two lets the model compare the last operation against the one
        /// before it, which is what catches a feature that went the wrong way.
        /// </summary>
        public int MaxImagesRetained { get; set; } = 2;

        /// <summary>Images dropped from history so far, for diagnostics.</summary>
        public int ImagesPruned { get; private set; }

        public int EntryCount => _entries.Count;

        public void AddUserText(string text)
        {
            _entries.Add(new Entry { Kind = EntryKind.UserText, Text = text });
        }

        public void AddAssistant(IReadOnlyList<ContentBlockParam> content)
        {
            _entries.Add(new Entry { Kind = EntryKind.Assistant, AssistantContent = content });
        }

        public void AddToolResults(IReadOnlyList<ToolResultEntry> results)
        {
            _entries.Add(new Entry { Kind = EntryKind.ToolResults, ToolResults = results });
        }

        /// <summary>
        /// Build the message list for the next request, applying the image
        /// retention policy.
        /// </summary>
        public List<MessageParam> BuildMessages()
        {
            // Work out which tool-result entries are allowed to keep images:
            // the most recent MaxImagesRetained entries that have any.
            var imageBearing = new List<int>();
            for (int i = 0; i < _entries.Count; i++)
            {
                var e = _entries[i];
                if (e.Kind == EntryKind.ToolResults && e.ToolResults.Any(r => r.HasImage))
                    imageBearing.Add(i);
            }

            var keepImagesAt = new HashSet<int>(
                imageBearing.Skip(Math.Max(0, imageBearing.Count - Math.Max(0, MaxImagesRetained))));

            ImagesPruned = imageBearing.Count - keepImagesAt.Count;

            // The last tool-result entry gets the conversation cache breakpoint.
            int lastToolResultIndex = -1;
            for (int i = _entries.Count - 1; i >= 0; i--)
            {
                if (_entries[i].Kind == EntryKind.ToolResults) { lastToolResultIndex = i; break; }
            }

            var messages = new List<MessageParam>();

            for (int i = 0; i < _entries.Count; i++)
            {
                var e = _entries[i];

                switch (e.Kind)
                {
                    case EntryKind.UserText:
                        messages.Add(new MessageParam { Role = Role.User, Content = e.Text });
                        break;

                    case EntryKind.Assistant:
                        messages.Add(new MessageParam
                        {
                            Role = Role.Assistant,
                            Content = e.AssistantContent.ToList(),
                        });
                        break;

                    case EntryKind.ToolResults:
                        bool keepImages = keepImagesAt.Contains(i);
                        // Cache the conversation up to the end of the most
                        // recent tool results. Each turn resends the whole
                        // history, so without this the agent pays full input
                        // rate for every previous feature on every new one -
                        // which is why a long part gets disproportionately
                        // expensive rather than linearly so.
                        bool cacheHere = i == lastToolResultIndex;

                        var results = e.ToolResults
                            .Select((r, idx) => BuildToolResult(
                                r, keepImages,
                                cacheBreakpoint: cacheHere && idx == e.ToolResults.Count - 1))
                            .ToList();

                        messages.Add(new MessageParam
                        {
                            Role = Role.User,
                            Content = results.Select(r => (ContentBlockParam)r).ToList(),
                        });
                        break;
                }
            }

            return messages;
        }

        private static ToolResultBlockParam BuildToolResult(ToolResultEntry r, bool keepImage, bool cacheBreakpoint = false)
        {
            // Text first, image second - deliberately. Numbers are ground truth;
            // the picture is the sanity check, and putting it first invites the
            // model to judge the part by how it looks.
            if (keepImage && r.HasImage)
            {
                return new ToolResultBlockParam
                {
                    ToolUseID = r.ToolUseId,
                    IsError = r.IsError,
                    CacheControl = cacheBreakpoint ? new CacheControlEphemeral() : null,
                    Content = new List<Block>
                    {
                        new TextBlockParam { Text = r.Text },
                        new ImageBlockParam
                        {
                            Source = new Base64ImageSource
                            {
                                MediaType = MediaType.ImagePng,
                                Data = Convert.ToBase64String(r.Png),
                            },
                        },
                    },
                };
            }

            string text = r.Text;
            if (r.HasImage)
            {
                // Say that a screenshot existed. Without this the model can
                // reasonably conclude the tool never returned one, and start
                // asking for screenshots it has already seen.
                text += "\n(Screenshot from this step omitted from history to save context.)";
            }

            return new ToolResultBlockParam
            {
                ToolUseID = r.ToolUseId,
                IsError = r.IsError,
                CacheControl = cacheBreakpoint ? new CacheControlEphemeral() : null,
                Content = text,
            };
        }

        private enum EntryKind { UserText, Assistant, ToolResults }

        private sealed class Entry
        {
            public EntryKind Kind;
            public string Text;
            public IReadOnlyList<ContentBlockParam> AssistantContent;
            public IReadOnlyList<ToolResultEntry> ToolResults;
        }
    }
}

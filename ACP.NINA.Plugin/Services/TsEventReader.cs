using NINA.Plugin.Interfaces;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;

namespace ACP.NINA.Plugin.Services {

    /// What one Target Scheduler event told us, reduced to the only thing
    /// Part F needs: which Target Scheduler target is this about?
    public class TsEventTarget {

        /// target.Id in the database, when the event carried one.
        public int? TargetId { get; set; }

        /// The target name, when the event carried one. Only ever used for the
        /// log line, never for the join: names are editable in the Target
        /// Scheduler UI and row ids are not.
        public string TargetName { get; set; }

        /// What the value was read out of, so a real night's log says which
        /// shape Target Scheduler actually publishes.
        public string Source { get; set; }

        public bool HasTargetId => TargetId.HasValue && TargetId.Value > 0;
    }

    /// Pulls the target out of a Target Scheduler pub/sub message.
    ///
    /// The payload shape is not documented anywhere that can be checked
    /// without a running NINA, and the research notes only say TargetStart
    /// "includes exposure metadata". So this reads the message the way you
    /// would read a stranger's JSON: try the obvious shapes in order, record
    /// in the log which one worked, and treat finding nothing as a normal
    /// outcome rather than an error.
    ///
    /// Finding nothing is survivable because of how the reporter uses it. An
    /// event with no readable target still means Target Scheduler is imaging,
    /// so the reporter falls back to reporting every plan it can see. That is
    /// the same work the five minute timer does, so the worst case of an
    /// unreadable payload is that every event behaves like a missed one.
    public static class TsEventReader {

        /// Property names that plausibly carry the target id, most specific
        /// first so "TargetId" wins over a bare "Id" on a payload with both.
        private static readonly string[] IdNames = {
            "TargetId", "TargetDatabaseId", "DatabaseId", "TargetPk", "Id",
        };

        private static readonly string[] NameNames = {
            "TargetName", "Name", "Target",
        };

        public static TsEventTarget Read(IMessage message) {
            var result = new TsEventTarget { Source = "nothing readable" };
            if (message == null) return result;

            // A message whose whole content is the id. The cheapest shape, and
            // the one a publisher writes when the event has one thing to say.
            var direct = AsTargetId(message.Content);
            if (direct.HasValue) {
                result.TargetId = direct;
                result.Source = "Content";
                return result;
            }

            // Custom headers, where NINA's own examples put the small scalar
            // facts about a message.
            if (message.CustomHeaders != null) {
                foreach (var key in IdNames) {
                    foreach (var kv in message.CustomHeaders) {
                        if (!string.Equals(kv.Key, key, StringComparison.OrdinalIgnoreCase)) continue;
                        var headerId = AsTargetId(kv.Value);
                        if (headerId.HasValue) {
                            result.TargetId = headerId;
                            result.Source = $"CustomHeaders[{kv.Key}]";
                            result.TargetName = ReadName(message.Content, message.CustomHeaders);
                            return result;
                        }
                    }
                }
            }

            // A payload object. Reflection rather than a cast, because the type
            // lives in Target Scheduler's assembly, which this plugin does not
            // reference and should not have to.
            var content = message.Content;
            if (content != null) {
                var props = content.GetType()
                    .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                    .Where(p => p.GetIndexParameters().Length == 0)
                    .ToList();
                foreach (var key in IdNames) {
                    var prop = props.FirstOrDefault(p =>
                        string.Equals(p.Name, key, StringComparison.OrdinalIgnoreCase));
                    if (prop == null) continue;
                    object value;
                    try {
                        value = prop.GetValue(content);
                    } catch (Exception) {
                        continue;
                    }
                    var propId = AsTargetId(value);
                    if (propId.HasValue) {
                        result.TargetId = propId;
                        result.Source = $"Content.{prop.Name}";
                        result.TargetName = ReadName(content, message.CustomHeaders);
                        return result;
                    }
                }
            }

            result.TargetName = ReadName(content, message.CustomHeaders);
            return result;
        }

        private static string ReadName(object content, IDictionary<string, object> headers) {
            if (headers != null) {
                foreach (var key in NameNames) {
                    foreach (var kv in headers) {
                        if (!string.Equals(kv.Key, key, StringComparison.OrdinalIgnoreCase)) continue;
                        var text = kv.Value as string;
                        if (!string.IsNullOrWhiteSpace(text)) return text;
                    }
                }
            }
            if (content == null) return null;
            if (content is string s) return s;

            foreach (var key in NameNames) {
                PropertyInfo prop;
                try {
                    prop = content.GetType()
                        .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                        .FirstOrDefault(p => p.GetIndexParameters().Length == 0
                            && string.Equals(p.Name, key, StringComparison.OrdinalIgnoreCase));
                } catch (Exception) {
                    return null;
                }
                if (prop == null) continue;
                try {
                    var text = prop.GetValue(content) as string;
                    if (!string.IsNullOrWhiteSpace(text)) return text;
                } catch (Exception) {
                    // A property that throws is not a name.
                }
            }
            return null;
        }

        /// Accepts the integer shapes a publisher might use, including the
        /// string one, and rejects anything that is not a positive row id.
        /// A bool is refused on purpose: Convert would happily make it 1.
        private static int? AsTargetId(object value) {
            if (value == null) return null;
            if (value is bool) return null;

            if (value is int i) return i > 0 ? i : (int?)null;
            if (value is long l) return l > 0 && l <= int.MaxValue ? (int)l : (int?)null;
            if (value is short sh) return sh > 0 ? (int)sh : (int?)null;
            if (value is uint ui) return ui > 0 && ui <= int.MaxValue ? (int)ui : (int?)null;

            if (value is string text) {
                if (int.TryParse(text.Trim(), NumberStyles.Integer,
                        CultureInfo.InvariantCulture, out var parsed)) {
                    return parsed > 0 ? parsed : (int?)null;
                }
                return null;
            }
            return null;
        }
    }
}

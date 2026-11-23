using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;

namespace PcOnSongBridge
{
    internal sealed class CommandDelivery
    {
        public readonly string Message, ObservationNote;
        public readonly long DispatchStarted, Acknowledged;
        public readonly long? ChartObserved;

        public CommandDelivery(string message, long dispatchStarted, long acknowledged, long? chartObserved, string observationNote)
        {
            Message = message;
            DispatchStarted = dispatchStarted;
            Acknowledged = acknowledged;
            ChartObserved = chartObserved;
            ObservationNote = observationNote;
        }
    }

    internal sealed class CommandHistoryEntry
    {
        public readonly DateTime When = DateTime.Now;
        public readonly string CommandId, Title, Phrase;
        public readonly bool Voice;
        public string Heard, Result, Note, ContextKey, ContextLabel;
        public long? PreparationStarted, PreparationEnded, SpeechStart, SpeechEnd, FeedbackAt;
        public bool? Worked;
        public CommandDelivery Delivery;

        public CommandHistoryEntry(string commandId, string title, string phrase, bool voice)
        {
            CommandId = commandId;
            Title = title;
            Phrase = phrase;
            Voice = voice;
        }

        private static void Interval(List<string> lines, string label, long? from, long? to, bool upperBound = false)
        {
            if (!from.HasValue || !to.HasValue || from.Value <= 0 || to < from) return;
            double milliseconds = (to.Value - from.Value) * 1000.0 / Stopwatch.Frequency;
            lines.Add(label + (upperBound ? " ≤ " : "  ") + milliseconds.ToString("N0", CultureInfo.CurrentCulture) + " ms");
        }

        public string Timings
        {
            get
            {
                var lines = new List<string>();
                if (Delivery != null)
                {
                    Interval(lines, "Speech end → observed chart change", SpeechEnd, Delivery.ChartObserved, true);
                    Interval(lines, "Speech end → Android acknowledgment", SpeechEnd, Delivery.Acknowledged);
                    Interval(lines, "Speech end → dispatch start", SpeechEnd, Delivery.DispatchStarted);
                    Interval(lines, "Dispatch → Android acknowledgment", Delivery.DispatchStarted, Delivery.Acknowledged);
                    Interval(lines, "Speech start → observed chart change", SpeechStart, Delivery.ChartObserved, true);
                    if (!SpeechEnd.HasValue) Interval(lines, "Dispatch → observed chart change", Delivery.DispatchStarted, Delivery.ChartObserved, true);
                    Interval(lines, "Acknowledgment → your feedback (includes reaction time)", Delivery.Acknowledged, FeedbackAt);
                }
                Interval(lines, "Starting chart read before the test", PreparationStarted, PreparationEnded);
                return String.Join(Environment.NewLine, lines);
            }
        }

        public string Outcome
        {
            get
            {
                if (Worked.HasValue)
                    return (Worked.Value ? "You confirmed it worked" : "You reported no effect") +
                        (Delivery != null && Delivery.ChartObserved.HasValue ? " · Chart change observed" : "");
                return Result;
            }
        }

        public string Summary
        {
            get
            {
                return String.Join(" · ", new[] { Title, Voice ? "Voice" : "Manual", Outcome, Timings.Replace(Environment.NewLine, " · ") }
                    .Where(s => !String.IsNullOrWhiteSpace(s)));
            }
        }

        public string RepeatedTiming(IEnumerable<CommandHistoryEntry> history)
        {
            if (!Voice || Delivery == null || !SpeechEnd.HasValue) return "";
            var comparable = history.Where(item => item.CommandId == CommandId && item.ContextKey == ContextKey &&
                item.Voice && item.SpeechEnd.HasValue && item.Delivery != null).ToList();
            var lines = new List<string>();
            foreach (bool observed in new[] { false, true })
            {
                var values = comparable.Select(item => (observed ? item.Delivery.ChartObserved : item.Delivery.Acknowledged) - item.SpeechEnd)
                    .Where(value => value.HasValue && value >= 0).Select(value => value.Value * 1000.0 / Stopwatch.Frequency)
                    .OrderBy(value => value).ToArray();
                if (values.Length < 2) continue;
                double median = (values[(values.Length - 1) / 2] + values[values.Length / 2]) / 2.0;
                lines.Add(String.Format(CultureInfo.CurrentCulture, "{0} comparable tests · {1} median {2:N0} ms · range {3:N0}–{4:N0} ms",
                    values.Length, observed ? "Chart observation upper-bound" : "Speech end to acknowledgment", median, values[0], values[values.Length - 1]));
            }
            return String.Join(Environment.NewLine, lines);
        }
    }
}

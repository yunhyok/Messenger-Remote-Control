using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace RemoteMonitorLink
{
    // API (phase 2 / diagnostic bundle):
    //   TranscriptComparison.Compare(fullText, transcript, tailLines = 600) — pure, no UI, no I/O, no logging.
    //       fullText   = the user/auto clipboard copy of the whole Output buffer (authoritative).
    //       transcript = what the local model claims it read; compared line by line against the tail of fullText.
    //   Lines           — one Line per compared transcript line, in transcript order.
    //   Exact / Normalized / Near / Missing / Empty / OrderPreserved — counters for the report.
    //   Render()        — Korean per-line report. CONTAINS SCREEN TEXT: UI and diagnostic bundle only, never the log.
    //   Summary()       — "T1|lines|exact|normalized|near|missing|empty|order" metadata only, safe for the log.
    //   SelfTest()      — called from LinkSelfTest.Run().
    // Line.MatchedIndex is the 0-based index into the FULL text's lines (not into the tail window), or -1.
    // Matching prefers exact, then whitespace-normalized, then nearest lines. Equal candidates after the
    // previous match win before earlier unused candidates, and each source occurrence can be consumed once.
    // ponytail: greedy occurrence alignment; use sequence alignment if ambiguous near-matching repeats require it.
    internal sealed class TranscriptComparison
    {
        internal const int MaxTranscriptLines = 2000;
        internal const int MaxTailLines = 20000;
        // Distance is computed on at most this many characters plus the length difference of the remainder,
        // so a pathological single line cannot make the comparison quadratic in the whole buffer.
        internal const int MaxComparedLength = 512;
        internal const int MaxDiffFragment = 80;

        internal sealed class Line
        {
            internal string Transcript;
            internal string Status;      // EXACT | NORMALIZED | NEAR | MISSING | EMPTY
            internal int MatchedIndex = -1;
            internal string Matched;
            internal int Distance;
            internal string Diff;
        }

        private readonly List<Line> lines = new List<Line>();
        internal IReadOnlyList<Line> Lines { get { return lines; } }
        internal int Exact { get; private set; }
        internal int Normalized { get; private set; }
        internal int Near { get; private set; }
        internal int Missing { get; private set; }
        internal int Empty { get; private set; }
        internal bool OrderPreserved { get; private set; }

        private TranscriptComparison() { OrderPreserved = true; }

        internal static TranscriptComparison Compare(string fullText, string transcript, int tailLines = 600)
        {
            var result = new TranscriptComparison();
            int window = tailLines < 1 ? 1 : (tailLines > MaxTailLines ? MaxTailLines : tailLines);
            string[] full = SplitLines(fullText);
            int start = full.Length > window ? full.Length - window : 0;
            int count = full.Length - start;
            var normalizedFull = new string[count];
            var used = new bool[count];
            for (int i = 0; i < count; i++) normalizedFull[i] = Normalize(full[start + i]);
            string[] spoken = SplitLines(transcript);
            int limit = spoken.Length > MaxTranscriptLines ? MaxTranscriptLines : spoken.Length;
            int previous = -1;
            for (int i = 0; i < limit; i++)
            {
                int next = previous < start ? 0 : previous - start + 1;
                Line line = MatchLine(full, normalizedFull, used, start, count, next, spoken[i]);
                result.lines.Add(line);
                switch (line.Status)
                {
                    case "EXACT": result.Exact++; break;
                    case "NORMALIZED": result.Normalized++; break;
                    case "NEAR": result.Near++; break;
                    case "EMPTY": result.Empty++; break;
                    default: result.Missing++; break;
                }
                if (line.MatchedIndex < 0) continue;
                used[line.MatchedIndex - start] = true;
                if (line.MatchedIndex < previous) result.OrderPreserved = false;
                previous = line.MatchedIndex;
            }
            return result;
        }

        private static Line MatchLine(string[] full, string[] normalizedFull, bool[] used,
            int start, int count, int next, string text)
        {
            var line = new Line { Transcript = text, Status = "MISSING", MatchedIndex = -1, Distance = -1 };
            if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(Normalize(text)))
            { line.Status = "EMPTY"; line.Distance = 0; return line; }
            for (int offset = 0; offset < count; offset++)
            {
                int i = (next + offset) % count;
                if (used[i]) continue;
                if (string.Equals(full[start + i], text, StringComparison.Ordinal))
                {
                    line.Status = "EXACT"; line.MatchedIndex = start + i; line.Matched = full[start + i]; line.Distance = 0;
                    return line;
                }
            }
            string normalized = Normalize(text);
            for (int offset = 0; offset < count; offset++)
            {
                int i = (next + offset) % count;
                if (used[i]) continue;
                if (string.Equals(normalizedFull[i], normalized, StringComparison.Ordinal))
                {
                    line.Status = "NORMALIZED"; line.MatchedIndex = start + i; line.Matched = full[start + i]; line.Distance = 0;
                    return line;
                }
            }
            int threshold = Math.Max(2, normalized.Length / 10);
            int best = int.MaxValue, bestIndex = -1;
            for (int offset = 0; offset < count; offset++)
            {
                int i = (next + offset) % count;
                if (used[i]) continue;
                string candidate = normalizedFull[i];
                if (candidate.Length == 0) continue;
                if (Math.Abs(candidate.Length - normalized.Length) > threshold) continue;
                int allowed = threshold < best - 1 ? threshold : best - 1;
                int distance = LevenshteinDistance(normalized, candidate, allowed);
                if (distance < 0 || distance >= best) continue;
                best = distance; bestIndex = i;
            }
            if (bestIndex >= 0 && best <= threshold)
            {
                line.Status = "NEAR"; line.MatchedIndex = start + bestIndex; line.Matched = full[start + bestIndex];
                line.Distance = best; line.Diff = Describe(normalized, normalizedFull[bestIndex]);
            }
            return line;
        }

        // Bounded two-row Levenshtein: returns -1 as soon as the distance cannot stay within limit.
        private static int LevenshteinDistance(string left, string right, int limit)
        {
            if (limit < 0) return -1;
            int extra = Math.Abs(Math.Max(0, left.Length - MaxComparedLength) - Math.Max(0, right.Length - MaxComparedLength));
            if (extra > limit) return -1;
            if (left.Length > MaxComparedLength) left = left.Substring(0, SafeCut(left, MaxComparedLength));
            if (right.Length > MaxComparedLength) right = right.Substring(0, SafeCut(right, MaxComparedLength));
            if (Math.Abs(left.Length - right.Length) + extra > limit) return -1;
            var previous = new int[right.Length + 1];
            var current = new int[right.Length + 1];
            for (int j = 0; j <= right.Length; j++) previous[j] = j;
            for (int i = 1; i <= left.Length; i++)
            {
                current[0] = i;
                int rowBest = current[0];
                for (int j = 1; j <= right.Length; j++)
                {
                    int cost = left[i - 1] == right[j - 1] ? 0 : 1;
                    int insert = current[j - 1] + 1, delete = previous[j] + 1, replace = previous[j - 1] + cost;
                    int value = Math.Min(Math.Min(insert, delete), replace);
                    current[j] = value;
                    if (value < rowBest) rowBest = value;
                }
                if (rowBest + extra > limit) return -1;
                int[] swap = previous; previous = current; current = swap;
            }
            int result = previous[right.Length] + extra;
            return result > limit ? -1 : result;
        }

        // Trim, then collapse every internal whitespace run to a single space. Case-sensitive on purpose:
        // PowerSI frequencies and units must not be matched across a case change.
        private static string Normalize(string text)
        {
            if (string.IsNullOrEmpty(text)) return string.Empty;
            var builder = new StringBuilder(text.Length);
            bool pending = false;
            for (int i = 0; i < text.Length; i++)
            {
                char letter = text[i];
                if (char.IsWhiteSpace(letter)) { pending = builder.Length > 0; continue; }
                if (pending) { builder.Append(' '); pending = false; }
                builder.Append(letter);
            }
            return builder.ToString();
        }

        private static string Describe(string left, string right)
        {
            int position = 0;
            while (position < left.Length && position < right.Length && left[position] == right[position]) position++;
            // The shared prefix can end between a surrogate pair; both sides hold the same char at position - 1.
            if (position > 0 && char.IsHighSurrogate(left[position - 1])) position--;
            return "@" + position.ToString(CultureInfo.InvariantCulture) +
                " [" + Fragment(left, position) + "] / [" + Fragment(right, position) + "]";
        }

        private static string Fragment(string text, int position)
        {
            if (position >= text.Length) return string.Empty;
            int length = text.Length - position;
            if (length > MaxDiffFragment) length = SafeCut(text, position + MaxDiffFragment) - position;
            return text.Substring(position, length);
        }

        // A cut between a high and a low surrogate would leave a lone surrogate in the rendered diff. Move it back
        // one char so whole characters are compared and displayed.
        private static int SafeCut(string text, int index)
        {
            return index > 0 && index < text.Length && char.IsHighSurrogate(text[index - 1]) &&
                char.IsLowSurrogate(text[index]) ? index - 1 : index;
        }

        private static string[] SplitLines(string text)
        {
            if (string.IsNullOrEmpty(text)) return new string[0];
            return text.Split(new[] { "\r\n", "\n", "\r" }, StringSplitOptions.None);
        }

        private static string Number(int value) { return value.ToString(CultureInfo.InvariantCulture); }

        // Metadata only: counts and one order flag, no transcript or Output text. Safe for the diagnostic log.
        internal string Summary()
        {
            var values = new[] { lines.Count, Exact, Normalized, Near, Missing, Empty, OrderPreserved ? 1 : 0 };
            var text = new StringBuilder("T1");
            for (int i = 0; i < values.Length; i++) text.Append('|').Append(Number(values[i]));
            return text.ToString();
        }

        // Contains the compared text. Screen/UI and diagnostic bundle only — never write this to the log.
        internal string Render()
        {
            var text = new StringBuilder();
            text.Append("전사본 ").Append(Number(lines.Count)).Append("행 대조 — 일치 ").Append(Number(Exact))
                .Append(" / 정규화 일치 ").Append(Number(Normalized)).Append(" / 근사 ").Append(Number(Near))
                .Append(" / 원문 대응 없음 ").Append(Number(Missing)).Append(" / 빈 줄 ").Append(Number(Empty))
                .Append(OrderPreserved ? " / 순서 유지" : " / 순서 어긋남").Append("\r\n");
            text.Append("[원본 Output 텍스트 포함 — 로그에 남기지 말고 화면/진단 번들에서만 확인]\r\n");
            text.Append("'원문 대응 없음'은 대응 원문 행을 찾지 못한 전사 행 수입니다. 동일 원문 행은 한 번만 대응합니다.\r\n")
                .Append("전체 줄·최신 줄 전사 여부: 미검증 — 실제 OCR 입력 이미지의 마지막 줄과 전사본을 비교하세요.\r\n")
                .Append("전사하지 않은 원문 행의 누락률이나 OCR 정확도는 계산하지 않습니다. 복사/캡처 시각 차이도 확인하세요.\r\n");
            for (int i = 0; i < lines.Count; i++)
            {
                Line line = lines[i];
                text.Append(Number(i + 1)).Append(' ').Append(Label(line.Status));
                if (line.MatchedIndex >= 0)
                {
                    text.Append(" (본문 ").Append(Number(line.MatchedIndex + 1)).Append("행");
                    if (line.Status == "NEAR") text.Append(", 거리 ").Append(Number(line.Distance));
                    text.Append(')');
                }
                text.Append("\r\n    전사: ").Append(line.Transcript ?? string.Empty).Append("\r\n");
                if (line.Matched != null) text.Append("    본문: ").Append(line.Matched).Append("\r\n");
                if (line.Diff != null) text.Append("    차이: ").Append(line.Diff).Append("\r\n");
            }
            return text.ToString();
        }

        private static string Label(string status)
        {
            switch (status)
            {
                case "EXACT": return "일치";
                case "NORMALIZED": return "정규화 일치";
                case "NEAR": return "근사";
                case "EMPTY": return "빈 줄";
                default: return "원문 대응 없음";
            }
        }

        internal static void SelfTest()
        {
            void Check(bool passed, string name)
            { if (!passed) throw new InvalidOperationException("Transcript comparison self-test: " + name); }

            const string full = "alpha line one\r\nbeta  line two\nGAMMA line three\rdelta line four\nepsilon line five";
            var mixed = Compare(full, "alpha line one\n  beta line two  \nGAMMA line thre\nzeta missing line\n   ");
            Check(mixed.Lines.Count == 5 && mixed.Exact == 1 && mixed.Normalized == 1 && mixed.Near == 1 &&
                mixed.Missing == 1 && mixed.Empty == 1, "every comparison status is reachable");
            Check(mixed.Lines[0].Status == "EXACT" && mixed.Lines[0].MatchedIndex == 0 && mixed.Lines[0].Distance == 0,
                "identical line matches in place");
            Check(mixed.Lines[1].Status == "NORMALIZED" && mixed.Lines[1].MatchedIndex == 1 &&
                mixed.Lines[1].Matched == "beta  line two", "whitespace-only difference is a normalized match");
            Check(mixed.Lines[2].Status == "NEAR" && mixed.Lines[2].MatchedIndex == 2 && mixed.Lines[2].Distance == 1 &&
                mixed.Lines[2].Diff != null && mixed.Lines[2].Diff.StartsWith("@", StringComparison.Ordinal),
                "one dropped character is a near match with a diff marker");
            Check(mixed.Lines[3].Status == "MISSING" && mixed.Lines[3].MatchedIndex == -1 && mixed.Lines[3].Matched == null,
                "unrelated transcript line is missing");
            Check(mixed.Lines[4].Status == "EMPTY" && mixed.Lines[4].MatchedIndex == -1, "blank transcript line is empty");
            Check(mixed.OrderPreserved && mixed.Summary() == "T1|5|1|1|1|1|1|1", "summary reports the counters and order flag");
            Check(mixed.Summary().IndexOf("alpha", StringComparison.Ordinal) < 0 &&
                mixed.Summary().IndexOf("line", StringComparison.Ordinal) < 0, "summary never carries compared text");
            string report = mixed.Render();
            Check(report.Contains("alpha line one") && report.Contains("beta  line two") && report.Contains("원문 대응 없음 1") &&
                report.Contains("순서 유지"), "rendered report keeps the per-line text for the bundle");

            var reordered = Compare(full, "GAMMA line three\nalpha line one");
            Check(reordered.Exact == 2 && !reordered.OrderPreserved && reordered.Summary().EndsWith("|0", StringComparison.Ordinal),
                "decreasing matched indexes break the order flag");
            var ordered = Compare(full, "alpha line one\nGAMMA line three");
            Check(ordered.OrderPreserved && ordered.Lines[1].MatchedIndex == 2, "increasing matched indexes keep the order flag");
            var repeated = Compare("same log\nsame log", "same log\nsame log");
            Check(repeated.Exact == 2 && repeated.Lines[0].MatchedIndex == 0 && repeated.Lines[1].MatchedIndex == 1 &&
                repeated.OrderPreserved, "repeated lines consume distinct occurrences in order");
            var suffix = Compare("A\nB\nA\nC", "B\nA\nC");
            Check(suffix.Exact == 3 && suffix.OrderPreserved && suffix.Lines[0].MatchedIndex == 1 &&
                suffix.Lines[1].MatchedIndex == 2 && suffix.Lines[2].MatchedIndex == 3,
                "a repeated line uses its later occurrence in an ordered suffix");
            var duplicate = Compare("A", "A\nA");
            Check(duplicate.Exact == 1 && duplicate.Missing == 1 && duplicate.Lines[1].MatchedIndex == -1,
                "an extra transcript occurrence cannot reuse the only source line");
            var mixedDuplicate = Compare("value = 38.000 MHz",
                "value = 38.000 MHz\n value = 38.000 MHz \nvalue = 38.001 MHz");
            Check(mixedDuplicate.Exact == 1 && mixedDuplicate.Missing == 2,
                "normalized and near matches cannot reuse a consumed source line");
            var normalizedRepeated = Compare("same  log\nsame  log", " same log \n same log ");
            Check(normalizedRepeated.Normalized == 2 && normalizedRepeated.Lines[1].MatchedIndex == 1 &&
                normalizedRepeated.OrderPreserved, "normalized repeats consume sequential occurrences");
            var nearRepeated = Compare("Simulation resumed.\nSimulation resumed.", "Simulation resumed\nSimulation resumed");
            Check(nearRepeated.Near == 2 && nearRepeated.Lines[1].MatchedIndex == 1 && nearRepeated.OrderPreserved,
                "equidistant near repeats consume sequential occurrences");
            var omitted = Compare("A\nB\nC", "A");
            Check(omitted.Exact == 1 && omitted.Missing == 0 && omitted.Summary() == "T1|1|1|0|0|0|0|1" &&
                omitted.Render().Contains("원문 대응 없음 0") && omitted.Render().Contains("전체 줄·최신 줄 전사 여부: 미검증") &&
                omitted.Render().Contains("OCR 정확도는 계산하지 않습니다") && !omitted.Render().Contains(" / 누락 "),
                "a matching partial transcript keeps the T1 contract without claiming complete OCR coverage");

            var empty = Compare(null, null);
            Check(empty.Lines.Count == 0 && empty.OrderPreserved && empty.Summary() == "T1|0|0|0|0|0|0|1" &&
                empty.Render().Contains("전사본 0행"), "empty inputs compare to an empty report");
            Check(Compare(null, "only a transcript").Missing == 1 && Compare("only a body", null).Lines.Count == 0,
                "one-sided inputs stay bounded");

            var builder = new StringBuilder("unique first marker\n");
            for (int i = 0; i < 700; i++) builder.Append("filler line ").Append(Number(i)).Append('\n');
            string wide = builder.ToString();
            Check(Compare(wide, "unique first marker").Missing == 1, "default tail window hides older full-text lines");
            var widened = Compare(wide, "unique first marker", 800);
            Check(widened.Exact == 1 && widened.Lines[0].MatchedIndex == 0, "widened tail window reaches the older line");
            Check(Compare(wide, "filler line 699").Exact == 1, "the newest full-text line is always inside the window");

            var many = new StringBuilder();
            for (int i = 0; i < MaxTranscriptLines + 100; i++) many.Append("zz\n");
            var capped = Compare("zz", many.ToString());
            Check(capped.Lines.Count == MaxTranscriptLines && capped.Exact == 1 && capped.Missing == MaxTranscriptLines - 1,
                "transcript line count is capped without reusing the source occurrence");

            var padded = Compare("value = 38.000 MHz", "   value   =   38.000   MHz   ");
            Check(padded.Normalized == 1 && padded.Lines[0].Distance == 0, "collapsed whitespace runs still match");
            var cased = Compare("Simulation resumed.", "simulation resumed.");
            Check(cased.Near == 1 && cased.Lines[0].Distance == 1, "case difference is a near match, never exact");
            var longLine = new string('x', MaxComparedLength + 200);
            var truncated = Compare(longLine, longLine + "yyy");
            Check(Compare(longLine, longLine).Exact == 1 && truncated.Near == 1 && truncated.Lines[0].Distance == 3,
                "over-long lines are compared by a bounded prefix plus the untruncated length difference");
            Check(Compare(longLine, longLine + new string('y', 200)).Missing == 1,
                "an over-long line never matches by its prefix alone");

            var emoji = new StringBuilder();
            for (int i = 0; i < 400; i++) emoji.Append("\U0001F600");
            string surrogateLine = "x" + emoji.ToString();     // 801 chars: the 512-char cut falls inside a pair.
            var leadingDiff = Compare(surrogateLine, "y" + emoji.ToString());
            Check(leadingDiff.Near == 1 && leadingDiff.Lines[0].Diff != null && NoLoneSurrogate(leadingDiff.Render()),
                "a truncated diff fragment never ends on a lone surrogate");
            var innerDiff = Compare(surrogateLine, "x\U0001F603" + emoji.ToString().Substring(2));
            Check(innerDiff.Near == 1 && innerDiff.Lines[0].Diff != null && NoLoneSurrogate(innerDiff.Render()),
                "a diff starting inside a surrogate pair never renders a lone surrogate");
            Check(NoLoneSurrogate(Compare(surrogateLine, surrogateLine).Render()) &&
                !NoLoneSurrogate("\uD83D"), "the lone-surrogate check itself is meaningful");
        }

        private static bool NoLoneSurrogate(string text)
        {
            for (int i = 0; i < text.Length; i++)
            {
                if (char.IsHighSurrogate(text[i]))
                {
                    if (i + 1 >= text.Length || !char.IsLowSurrogate(text[i + 1])) return false;
                    i++;
                }
                else if (char.IsLowSurrogate(text[i])) return false;
            }
            return true;
        }
    }
}

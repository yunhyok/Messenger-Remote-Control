using System;
using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace RemoteMonitorMaster
{
    internal enum PowerSiCompletionState { NoMarkers, Running, FinishPending, Finished }

    internal sealed class PowerSiCompletionResult
    {
        internal readonly PowerSiCompletionState State;
        internal readonly long SamplingPoints; // 0 unless Finished.
        internal readonly string LastFrequencyLine; // Trimmed Output text for the phone reply only; never log it.
        internal readonly int MarkerLines;

        internal PowerSiCompletionResult(PowerSiCompletionState state, long samplingPoints, string lastFrequencyLine,
            int markerLines)
        {
            State = state;
            SamplingPoints = state == PowerSiCompletionState.Finished ? samplingPoints : 0;
            LastFrequencyLine = lastFrequencyLine;
            MarkerLines = markerLines;
        }
    }

    // ponytail: completion is only the last whole-line PowerSI AFS marker block in directly read Output text.
    // Screen/LLM text (OCR) is untrusted data and is never judged; CPU, elapsed time or process existence never imply completion.
    internal static class PowerSiCompletion
    {
        private const string BufferSource = "BUFFER"; // PowerSiTargetReport.Source values for direct Output reads.
        private const string AutoCopySource = "AUTO_COPY";
        private const int MaxMarkerLineLength = 256; // Real marker lines are short; longer lines are never markers.
        private const int MaxSamplingDigits = 18; // Always fits in a long.

        private enum LineKind { None, Frequency, Finished, Total }

        internal static bool IsJudgeableSource(string source)
        {
            return string.Equals(source, BufferSource, StringComparison.Ordinal) ||
                string.Equals(source, AutoCopySource, StringComparison.Ordinal);
        }

        // Single linear pass. CR, LF and CRLF end a line; each line is trimmed and must be exactly one marker.
        internal static PowerSiCompletionResult Evaluate(string outputText)
        {
            if (string.IsNullOrEmpty(outputText))
                return new PowerSiCompletionResult(PowerSiCompletionState.NoMarkers, 0, null, 0);

            var state = PowerSiCompletionState.NoMarkers;
            long points = 0;
            int markers = 0;
            int frequencyStart = -1, frequencyEnd = -1;
            int length = outputText.Length;
            int lineStart = 0;
            while (true)
            {
                int lineEnd = lineStart;
                while (lineEnd < length && outputText[lineEnd] != '\r' && outputText[lineEnd] != '\n') lineEnd++;

                int start = lineStart, end = lineEnd;
                while (start < end && char.IsWhiteSpace(outputText[start])) start++;
                while (end > start && char.IsWhiteSpace(outputText[end - 1])) end--;
                long total = 0;
                switch (start == end || end - start > MaxMarkerLineLength ? LineKind.None :
                    Classify(outputText, start, end, out total))
                {
                    case LineKind.Frequency:
                        markers++;
                        state = PowerSiCompletionState.Running;
                        points = 0;
                        frequencyStart = start;
                        frequencyEnd = end;
                        break;
                    case LineKind.Finished:
                        markers++;
                        state = PowerSiCompletionState.FinishPending;
                        points = 0;
                        break;
                    case LineKind.Total:
                        markers++;
                        // A Total line only completes a block that an earlier AFS Finished opened.
                        if (state == PowerSiCompletionState.FinishPending || state == PowerSiCompletionState.Finished)
                        {
                            state = PowerSiCompletionState.Finished;
                            points = total;
                        }
                        break;
                }

                if (lineEnd >= length) break;
                lineStart = lineEnd + (outputText[lineEnd] == '\r' && lineEnd + 1 < length &&
                    outputText[lineEnd + 1] == '\n' ? 2 : 1);
            }

            return new PowerSiCompletionResult(state, points,
                frequencyStart < 0 ? null : outputText.Substring(frequencyStart, frequencyEnd - frequencyStart), markers);
        }

        // Grammar (case-sensitive, whitespace = char.IsWhiteSpace; "_" = at least one, "~" = optional):
        //   AFS_Current_Frequency~(~GHz|MHz~)~=~NUMBER     NUMBER = [+-]?(d+(.d*)?|.d+)([eE][+-]?d+)?
        //   AFS_Finished
        //   Total_Sampling_Points~=~DIGITS                 DIGITS = 1..18 ASCII digits
        private static LineKind Classify(string text, int start, int end, out long total)
        {
            total = 0;
            int p = start;
            if (Word(text, ref p, end, "AFS"))
            {
                if (!Spaces(text, ref p, end)) return LineKind.None;
                if (Word(text, ref p, end, "Finished")) return p == end ? LineKind.Finished : LineKind.None;
                if (!Word(text, ref p, end, "Current") || !Spaces(text, ref p, end) ||
                    !Word(text, ref p, end, "Frequency")) return LineKind.None;
                OptionalSpaces(text, ref p, end);
                if (!Symbol(text, ref p, end, '(')) return LineKind.None;
                OptionalSpaces(text, ref p, end);
                if (!Word(text, ref p, end, "GHz") && !Word(text, ref p, end, "MHz")) return LineKind.None;
                OptionalSpaces(text, ref p, end);
                if (!Symbol(text, ref p, end, ')')) return LineKind.None;
                OptionalSpaces(text, ref p, end);
                if (!Symbol(text, ref p, end, '=')) return LineKind.None;
                OptionalSpaces(text, ref p, end);
                return Number(text, ref p, end) && p == end ? LineKind.Frequency : LineKind.None;
            }

            if (!Word(text, ref p, end, "Total") || !Spaces(text, ref p, end) ||
                !Word(text, ref p, end, "Sampling") || !Spaces(text, ref p, end) ||
                !Word(text, ref p, end, "Points")) return LineKind.None;
            OptionalSpaces(text, ref p, end);
            if (!Symbol(text, ref p, end, '=')) return LineKind.None;
            OptionalSpaces(text, ref p, end);
            int digitsStart = p;
            long value = 0;
            while (p < end && IsAsciiDigit(text[p]))
            {
                if (p - digitsStart >= MaxSamplingDigits) return LineKind.None;
                value = value * 10 + (text[p] - '0');
                p++;
            }
            if (p == digitsStart || p != end) return LineKind.None;
            total = value;
            return LineKind.Total;
        }

        private static bool Word(string text, ref int p, int end, string word)
        {
            if (end - p < word.Length || string.CompareOrdinal(text, p, word, 0, word.Length) != 0) return false;
            p += word.Length;
            return true;
        }

        private static bool Symbol(string text, ref int p, int end, char symbol)
        {
            if (p >= end || text[p] != symbol) return false;
            p++;
            return true;
        }

        private static bool Spaces(string text, ref int p, int end)
        {
            int before = p;
            OptionalSpaces(text, ref p, end);
            return p > before;
        }

        private static void OptionalSpaces(string text, ref int p, int end)
        {
            while (p < end && char.IsWhiteSpace(text[p])) p++;
        }

        private static bool Number(string text, ref int p, int end)
        {
            int q = p;
            if (q < end && (text[q] == '+' || text[q] == '-')) q++;
            int integerDigits = Digits(text, ref q, end);
            int fractionDigits = 0;
            if (q < end && text[q] == '.')
            {
                q++;
                fractionDigits = Digits(text, ref q, end);
            }
            if (integerDigits == 0 && fractionDigits == 0) return false;
            if (q < end && (text[q] == 'e' || text[q] == 'E'))
            {
                q++;
                if (q < end && (text[q] == '+' || text[q] == '-')) q++;
                if (Digits(text, ref q, end) == 0) return false;
            }
            p = q;
            return true;
        }

        private static int Digits(string text, ref int p, int end)
        {
            int start = p;
            while (p < end && IsAsciiDigit(text[p])) p++;
            return p - start;
        }

        private static bool IsAsciiDigit(char value)
        {
            return value >= '0' && value <= '9';
        }

        internal static void RunSelfTest()
        {
            string[] frequencies =
            {
                "AFS Current Frequency ( GHz ) = 1.515", "AFS Current Frequency ( MHz ) = 7.500",
                "AFS Current Frequency ( GHz ) = 3.0225", "AFS Current Frequency ( GHz ) = 0.7612",
                "AFS Current Frequency ( MHz ) = 15.000", "AFS Current Frequency ( GHz ) = 2.2687",
                "AFS Current Frequency ( GHz ) = 1.1381", "AFS Current Frequency ( MHz ) = 381.25",
                "AFS Current Frequency ( GHz ) = 2.6456", "AFS Current Frequency ( GHz ) = 1.8918",
                "AFS Current Frequency ( GHz ) = 0.3847"
            };
            var sample = new StringBuilder("PowerSI solver started\r\nSweep setup complete\r\n");
            foreach (string line in frequencies) sample.Append(line).Append("\r\n").Append("  Solving matrix...\r\n");
            sample.Append("AFS Finished\r\nWriting results\r\nTotal Sampling Points = 118\r\n");
            PowerSiCompletionResult result = Evaluate(sample.ToString());
            Need(result.State == PowerSiCompletionState.Finished && result.SamplingPoints == 118 && result.MarkerLines == 13 &&
                result.LastFrequencyLine == "AFS Current Frequency ( GHz ) = 0.3847", "USER_SAMPLE");

            result = Evaluate("AFS Current Frequency ( MHz ) = 7.500\nAFS Current Frequency ( MHz ) = 12.5\n");
            Need(result.State == PowerSiCompletionState.Running && result.SamplingPoints == 0 && result.MarkerLines == 2 &&
                result.LastFrequencyLine == "AFS Current Frequency ( MHz ) = 12.5", "MHZ_RUNNING");

            result = Evaluate("AFS Current Frequency ( GHz ) = 1\nAFS Finished\nTotal Sampling Points = 118\n" +
                "AFS Current Frequency ( GHz ) = 2.5\n");
            Need(result.State == PowerSiCompletionState.Running && result.SamplingPoints == 0 && result.MarkerLines == 4 &&
                result.LastFrequencyLine == "AFS Current Frequency ( GHz ) = 2.5", "NEW_SWEEP_AFTER_FINISH");

            result = Evaluate("AFS Current Frequency ( GHz ) = 1\nAFS Finished\nWriting results\n");
            Need(result.State == PowerSiCompletionState.FinishPending && result.SamplingPoints == 0 && result.MarkerLines == 2,
                "FINISH_WITHOUT_TOTAL");

            result = Evaluate("AFS Current Frequency ( GHz ) = 1\nTotal Sampling Points = 118\nAFS Finished\n");
            Need(result.State == PowerSiCompletionState.FinishPending && result.SamplingPoints == 0 && result.MarkerLines == 3,
                "TOTAL_BEFORE_FINISH");

            result = Evaluate("AFS Finished\nTotal Sampling Points = 50\nAFS Finished\n");
            Need(result.State == PowerSiCompletionState.FinishPending && result.SamplingPoints == 0, "LAST_FINISH_DECIDES");

            result = Evaluate("AFS Finished\nTotal Sampling Points = 50\nnoise\nTotal Sampling Points = 118");
            Need(result.State == PowerSiCompletionState.Finished && result.SamplingPoints == 118 && result.MarkerLines == 3 &&
                result.LastFrequencyLine == null, "LAST_TOTAL_AFTER_FINISH");

            result = Evaluate("Total Sampling Points = 118\n");
            Need(result.State == PowerSiCompletionState.NoMarkers && result.MarkerLines == 1, "TOTAL_ONLY");

            foreach (string rejected in new[]
            {
                "afs finished", "AFS finished", "AFS FINISHED", "total sampling points = 5", "Total sampling Points = 5",
                "AFS current frequency ( GHz ) = 1", "AFS Current Frequency ( ghz ) = 1", "AFS Current Frequency ( GHZ ) = 1",
                "Info: AFS Finished", "AFS Finished.", "AFS Finished now", "xAFS Finished", "AFSFinished", "AFS Finished!",
                "Total Sampling Points = 118 points", "Total Sampling Points = 118.", "[AFS Finished]",
                "AFS Current Frequency ( GHz ) = 1.5 GHz", "AFS Current Frequency ( kHz ) = 1", "AFS Current Frequency GHz = 1",
                "AFS Current Frequency ( GHz ) =", "AFS Current Frequency ( GHz ) = abc", "AFS Current Frequency ( GHz ) 1.5",
                "AFS CurrentFrequency ( GHz ) = 1", "AFS Current Frequency ( GHz ) = 1e", "AFS Current Frequency ( GHz ) = .",
                "AFS Current Frequency ( GHz ) = 1.2.3", "AFS Current Frequency ( GHz ) = e3", "AFS Current Frequency ( GHz ) = --1",
                "AFS Current Frequency ( GHz  = 1", "AFS Current Frequency ( GHz ) == 1", "Total Sampling Points = -5",
                "Total Sampling Points = 1.5", "Total Sampling Points = +5", "Total Sampling Points =", "Total Sampling Points 118",
                "TotalSampling Points = 1", "AFS\u0000Finished", "AFS" + new string(' ', 300) + "Finished"
            })
            {
                result = Evaluate(rejected);
                Need(result.State == PowerSiCompletionState.NoMarkers && result.MarkerLines == 0 && result.LastFrequencyLine == null,
                    "REJECTED_" + rejected.Length.ToString(CultureInfo.InvariantCulture));
            }

            result = Evaluate("AFS Finished\nTotal Sampling Points = 9999999999999999999\n");
            Need(result.State == PowerSiCompletionState.FinishPending && result.MarkerLines == 1, "TOTAL_OVERFLOW_REJECTED");
            result = Evaluate("AFS Finished\nTotal Sampling Points = 000000000000000118\n");
            Need(result.State == PowerSiCompletionState.Finished && result.SamplingPoints == 118, "TOTAL_EIGHTEEN_DIGITS");

            foreach (string number in new[] { "1", "+1", "-1", "1.", ".5", "1.5", "1e3", "1E+09", "-7.5E-3", "0.000" })
            {
                result = Evaluate("AFS Current Frequency ( GHz ) = " + number);
                Need(result.State == PowerSiCompletionState.Running && result.MarkerLines == 1, "NUMBER_" + number);
            }

            result = Evaluate(" \t AFS\t Current  Frequency(GHz)=1.5e+09 \t\n\tAFS \t Finished\t\n" +
                "Total\tSampling  Points=118   \n   AFS Current Frequency (  MHz  )  =  -7.5E-3\n");
            Need(result.State == PowerSiCompletionState.Running && result.MarkerLines == 4 &&
                result.LastFrequencyLine == "AFS Current Frequency (  MHz  )  =  -7.5E-3", "WHITESPACE_VARIANTS");

            string[] block = { "AFS Current Frequency ( GHz ) = 1.515", "AFS Finished", "Total Sampling Points = 118" };
            foreach (string newline in new[] { "\r\n", "\r", "\n" })
            {
                result = Evaluate(newline + newline + "  " + newline + string.Join(newline, block) + newline + newline + " \t" + newline);
                Need(result.State == PowerSiCompletionState.Finished && result.SamplingPoints == 118 && result.MarkerLines == 3,
                    "NEWLINE_" + newline.Length.ToString(CultureInfo.InvariantCulture) + (newline[0] == '\r' ? "CR" : "LF"));
                result = Evaluate(string.Join(newline, block));
                Need(result.State == PowerSiCompletionState.Finished && result.SamplingPoints == 118, "NO_TRAILING_NEWLINE");
            }
            result = Evaluate("AFS Current Frequency ( GHz ) = 1\r\nAFS Finished\rTotal Sampling Points = 7\n\r");
            Need(result.State == PowerSiCompletionState.Finished && result.SamplingPoints == 7 && result.MarkerLines == 3,
                "MIXED_NEWLINES");
            result = Evaluate("AFS Finished Total Sampling Points = 7");
            Need(result.State == PowerSiCompletionState.NoMarkers && result.MarkerLines == 0, "ONE_LINE_NOT_SPLIT");

            foreach (string empty in new[] { null, string.Empty, " ", "\r\n", "\n\n\n", " \t\r\n\t " })
            {
                result = Evaluate(empty);
                Need(result.State == PowerSiCompletionState.NoMarkers && result.MarkerLines == 0 &&
                    result.LastFrequencyLine == null && result.SamplingPoints == 0, "EMPTY");
            }

            const int MaxOutput = 8 * 1024 * 1024; // PowerSiReport.MaxOutputLength
            const string repeated = "AFS Current Frequency ( GHz ) = 1.515\r\n";
            int count = MaxOutput / repeated.Length;
            var large = new StringBuilder(MaxOutput);
            for (int i = 0; i < count; i++) large.Append(repeated);
            large.Append('x', MaxOutput - large.Length);
            string largeText = large.ToString();
            Need(largeText.Length == MaxOutput, "LARGE_INPUT_SIZE");
            var clock = Stopwatch.StartNew();
            result = Evaluate(largeText);
            Need(result.State == PowerSiCompletionState.Running && result.MarkerLines == count &&
                result.LastFrequencyLine == "AFS Current Frequency ( GHz ) = 1.515", "LARGE_RUNNING");
            result = Evaluate(new string('A', MaxOutput - 12) + " Finished");
            Need(result.State == PowerSiCompletionState.NoMarkers, "LARGE_SINGLE_LINE");
            result = Evaluate(new string('\r', MaxOutput));
            Need(result.State == PowerSiCompletionState.NoMarkers && result.MarkerLines == 0, "LARGE_EMPTY_LINES");
            result = Evaluate("AFS " + new string(' ', MaxOutput - 16) + "Finished");
            Need(result.State == PowerSiCompletionState.NoMarkers, "LARGE_WHITESPACE_LINE");
            Need(clock.Elapsed < TimeSpan.FromSeconds(30), "LARGE_INPUT_TIME");

            Need(IsJudgeableSource("BUFFER") && IsJudgeableSource("AUTO_COPY") && !IsJudgeableSource("OCR") &&
                !IsJudgeableSource("NONE") && !IsJudgeableSource(null) && !IsJudgeableSource(string.Empty) &&
                !IsJudgeableSource("buffer") && !IsJudgeableSource(" BUFFER") && !IsJudgeableSource("AUTO_COPY ") &&
                !IsJudgeableSource("AUTOCOPY"), "JUDGEABLE_SOURCE_TABLE");
        }

        private static void Need(bool condition, string reason)
        {
            if (!condition) throw new InvalidOperationException("PowerSI completion self-test failed: " + reason + ".");
        }
    }
}

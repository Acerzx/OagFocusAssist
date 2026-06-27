using System;
using System.Collections.Generic;
using System.Linq;

namespace OagFocusAssist.Analyzers;

public enum FocusState
{
    Collecting,
    Improving,
    Plateau,
    Optimal,
    Degraded
}

public static class HfrAnalyzer
{
    public sealed record AnalysisResult(
        double CurrentHfr = 0,
        double AverageHfr = 0,
        double MinHfr = 0,
        double MaxHfr = 0,
        int MinHfrIndex = -1,
        string TrendText = "—",
        FocusState State = FocusState.Collecting,
        bool ShouldSuggestFinish = false,
        string SuggestionReason = "");

    public static AnalysisResult Analyze(IList<double> values)
    {
        if (values is null || values.Count is 0) return new();

        double currentHfr = values[^1];
        double averageHfr = values.Average();
        double minHfr = values.Min();
        double maxHfr = values.Max();
        int minHfrIndex = values.IndexOf(minHfr);

        string trendText = CalcTrend(values, averageHfr);
        var state = CalcState(values, minHfr, minHfrIndex);
        var (shouldSuggest, reason) = CalcSuggestion(values, minHfr, minHfrIndex);

        return new AnalysisResult(
            CurrentHfr: currentHfr,
            AverageHfr: averageHfr,
            MinHfr: minHfr,
            MaxHfr: maxHfr,
            MinHfrIndex: minHfrIndex,
            TrendText: trendText,
            State: state,
            ShouldSuggestFinish: shouldSuggest,
            SuggestionReason: reason);
    }

    private static string CalcTrend(IList<double> values, double avg)
    {
        // УБРАНО: тире "➖" перед "Сбор данных..."
        if (values.Count < 3) return "Сбор данных...";

        int windowSize = Math.Min(5, values.Count);
        var window = values.Skip(values.Count - windowSize).ToList();

        double sumX = 0, sumY = 0, sumXY = 0, sumX2 = 0;
        int n = window.Count;
        for (int i = 0; i < n; i++)
        {
            sumX += i;
            sumY += window[i];
            sumXY += i * window[i];
            sumX2 += i * i;
        }

        double denom = n * sumX2 - sumX * sumX;
        if (Math.Abs(denom) < 1e-9) return "Плато";

        double slope = (n * sumXY - sumX * sumY) / denom;
        double normalizedSlope = slope / (avg + 1e-6);

        return normalizedSlope switch
        {
            < -0.02 => "Улучшается",
            > 0.02 => "Ухудшается",
            _ => "Плато"
        };
    }

    private static FocusState CalcState(IList<double> values, double minHfr, int minIndex)
    {
        if (values.Count < 5) return FocusState.Collecting;

        double degradationThreshold = minHfr * 1.10;
        int recentDegraded = 0;
        int lookBack = Math.Min(3, values.Count - minIndex - 1);

        if (minIndex >= 0 && lookBack > 0)
        {
            for (int i = values.Count - lookBack; i < values.Count; i++)
            {
                if (values[i] > degradationThreshold) recentDegraded++;
            }
        }

        if (minIndex > 0 && minIndex < values.Count - 2 && recentDegraded >= 2)
        {
            return FocusState.Degraded;
        }

        int pointsAfterMin = values.Count - minIndex - 1;
        if (pointsAfterMin >= 2)
        {
            double threshold = minHfr * 1.05;
            int risingPoints = 0;
            for (int i = minIndex + 1; i < values.Count; i++)
            {
                if (values[i] > threshold) risingPoints++;
            }
            if (risingPoints >= 2) return FocusState.Optimal;
        }

        if (pointsAfterMin >= 4)
        {
            var plateau = values.Skip(minIndex).Take(5).ToList();
            double stddev = CalcStdDev(plateau);
            if (stddev < minHfr * 0.02) return FocusState.Plateau;
        }

        if (values.Count >= 3)
        {
            var last3 = values.Skip(values.Count - 3).ToList();
            if (last3[2] < last3[0] - 0.05) return FocusState.Improving;
        }

        return FocusState.Collecting;
    }

    private static (bool shouldSuggest, string reason) CalcSuggestion(
        IList<double> values, double minHfr, int minIndex)
    {
        if (values.Count < 5 || minIndex < 2) return (false, "");

        int pointsAfterMin = values.Count - minIndex - 1;
        if (pointsAfterMin < 2) return (false, "");

        double threshold = minHfr * 1.05;
        int risingPoints = 0;
        for (int i = minIndex + 1; i < values.Count; i++)
        {
            if (values[i] > threshold) risingPoints++;
        }

        if (risingPoints >= 2)
        {
            return (true,
                $"Оптимум в точке #{minIndex + 1} (HFR={minHfr:F2}). " +
                $"После оптимума {risingPoints} точек роста.");
        }

        if (pointsAfterMin >= 4)
        {
            var plateau = values.Skip(minIndex).Take(5).ToList();
            double stddev = CalcStdDev(plateau);
            if (stddev < minHfr * 0.02)
            {
                return (true, $"Стабильное плато на HFR={minHfr:F2}.");
            }
        }

        return (false, "");
    }

    private static double CalcStdDev(IList<double> values)
    {
        double avg = values.Average();
        double sumSquares = values.Sum(v => (v - avg) * (v - avg));
        return Math.Sqrt(sumSquares / values.Count);
    }
}
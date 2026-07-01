using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace TbotUltra.Desktop;

public sealed record DailyPacingDayRow(
    string Date,
    string Online,
    string Waiting,
    string Limit,
    string Usage);

public sealed record DailyPacingTaskRow(
    string Task,
    int Runs,
    string LastRun,
    string PeakHour);

public sealed record DailyPacingTimelineRow(
    string Date,
    string Time,
    string State,
    string Duration,
    string Details);

/// <summary>One day on the runtime graph: online hours, and the day's daily-max (limit) hours if set.</summary>
public sealed record DailyPacingChartPoint(
    string DateLabel,
    double OnlineHours,
    double? LimitHours);

public partial class DailyPacingDetailsWindow : Window
{
    private readonly IReadOnlyList<DailyPacingChartPoint> _chartPoints;

    public DailyPacingDetailsWindow(
        string onlineToday,
        string waitingToday,
        string timeLeft,
        string dailyLimit,
        string weekTotal,
        string accountTotal,
        IReadOnlyList<DailyPacingDayRow> dayRows,
        IReadOnlyList<DailyPacingTaskRow> taskRows,
        IReadOnlyList<DailyPacingTimelineRow> timelineRows,
        IReadOnlyList<DailyPacingChartPoint> chartPoints)
    {
        InitializeComponent();
        OnlineTodayTextBlock.Text = onlineToday;
        WaitingTodayTextBlock.Text = waitingToday;
        TimeLeftTextBlock.Text = timeLeft;
        DailyLimitTextBlock.Text = dailyLimit;
        WeekTotalTextBlock.Text = weekTotal;
        AccountTotalTextBlock.Text = accountTotal;
        WeekDataGrid.ItemsSource = dayRows;
        TaskDataGrid.ItemsSource = taskRows;
        TimelineDataGrid.ItemsSource = timelineRows;
        _chartPoints = chartPoints;
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void ChartCanvas_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        RenderChart();
    }

    // Draws a bar-per-day runtime chart: green bars (online time), a dashed amber "Max" reference line
    // (average daily limit), and a blue linear trend line. Re-rendered on every resize.
    private void RenderChart()
    {
        var canvas = ChartCanvas;
        canvas.Children.Clear();

        var width = canvas.ActualWidth;
        var height = canvas.ActualHeight;
        if (width < 60 || height < 60 || _chartPoints.Count == 0)
        {
            return;
        }

        var axisBrush = (Brush)FindResource("BorderBrush");
        var labelBrush = (Brush)FindResource("TextSubtleBrush");
        var barBrush = (Brush)FindResource("AccentBrush");
        var trendBrush = (Brush)FindResource("InfoBrush");
        var maxBrush = (Brush)FindResource("WarningBrush");

        const double leftPad = 46;
        const double rightPad = 12;
        const double topPad = 26;
        const double bottomPad = 26;
        var plotW = width - leftPad - rightPad;
        var plotH = height - topPad - bottomPad;
        if (plotW <= 20 || plotH <= 20)
        {
            return;
        }

        // Y scale: fit the largest of online or limit, rounded up to an even number of hours.
        var maxOnline = _chartPoints.Max(p => p.OnlineHours);
        var maxLimit = _chartPoints.Where(p => p.LimitHours.HasValue).Select(p => p.LimitHours!.Value).DefaultIfEmpty(0).Max();
        var yMax = Math.Max(Math.Max(maxOnline, maxLimit), 1.0);
        yMax = Math.Max(2.0, Math.Ceiling(yMax / 2.0) * 2.0);

        double X(int index) => leftPad + plotW * (index + 0.5) / _chartPoints.Count;
        double Y(double hours) => topPad + plotH * (1 - Math.Clamp(hours, 0, yMax) / yMax);

        // Horizontal gridlines + Y-axis hour labels.
        const int yTicks = 4;
        for (var tick = 0; tick <= yTicks; tick++)
        {
            var hours = yMax * tick / yTicks;
            var y = Y(hours);
            canvas.Children.Add(new Line
            {
                X1 = leftPad,
                X2 = leftPad + plotW,
                Y1 = y,
                Y2 = y,
                Stroke = axisBrush,
                StrokeThickness = 1,
                Opacity = tick == 0 ? 0.9 : 0.3,
            });
            AddText(canvas, $"{hours:0}h", 6, y - 8, labelBrush, 10);
        }

        // Bars (online time per day).
        var slot = plotW / _chartPoints.Count;
        var barWidth = Math.Max(2, Math.Min(26, slot * 0.62));
        for (var i = 0; i < _chartPoints.Count; i++)
        {
            var top = Y(_chartPoints[i].OnlineHours);
            var bar = new Rectangle
            {
                Width = barWidth,
                Height = Math.Max(0, topPad + plotH - top),
                Fill = barBrush,
                RadiusX = 2,
                RadiusY = 2,
            };
            Canvas.SetLeft(bar, X(i) - barWidth / 2);
            Canvas.SetTop(bar, top);
            canvas.Children.Add(bar);
        }

        // X-axis date labels, thinned out so they never overlap.
        var labelEvery = Math.Max(1, (int)Math.Ceiling(_chartPoints.Count / Math.Max(1.0, plotW / 52.0)));
        for (var i = 0; i < _chartPoints.Count; i++)
        {
            if (i % labelEvery != 0 && i != _chartPoints.Count - 1)
            {
                continue;
            }

            AddText(canvas, _chartPoints[i].DateLabel, X(i) - 15, topPad + plotH + 5, labelBrush, 9);
        }

        // "Max" reference line at the average configured daily limit (only when a limit exists).
        var limits = _chartPoints.Where(p => p.LimitHours is > 0).Select(p => p.LimitHours!.Value).ToList();
        if (limits.Count > 0)
        {
            var avgLimit = limits.Average();
            var y = Y(avgLimit);
            canvas.Children.Add(new Line
            {
                X1 = leftPad,
                X2 = leftPad + plotW,
                Y1 = y,
                Y2 = y,
                Stroke = maxBrush,
                StrokeThickness = 1.5,
                StrokeDashArray = new DoubleCollection { 4, 3 },
            });
            AddText(canvas, $"Max {avgLimit:0.#}h", leftPad + plotW - 64, y - 14, maxBrush, 10);
        }

        // Linear-regression trend line of online hours over the day index.
        if (_chartPoints.Count >= 2)
        {
            var n = _chartPoints.Count;
            double sumX = 0, sumY = 0, sumXy = 0, sumXx = 0;
            for (var i = 0; i < n; i++)
            {
                var value = _chartPoints[i].OnlineHours;
                sumX += i;
                sumY += value;
                sumXy += i * value;
                sumXx += (double)i * i;
            }

            var denom = n * sumXx - sumX * sumX;
            if (Math.Abs(denom) > 1e-9)
            {
                var slope = (n * sumXy - sumX * sumY) / denom;
                var intercept = (sumY - slope * sumX) / n;
                var trend = new Polyline
                {
                    Stroke = trendBrush,
                    StrokeThickness = 2,
                    StrokeLineJoin = PenLineJoin.Round,
                };
                trend.Points.Add(new Point(X(0), Y(slope * 0 + intercept)));
                trend.Points.Add(new Point(X(n - 1), Y(slope * (n - 1) + intercept)));
                canvas.Children.Add(trend);
            }
        }

        AddLegend(canvas, leftPad, 4, barBrush, maxBrush, trendBrush, labelBrush);
    }

    private static void AddText(Canvas canvas, string text, double x, double y, Brush brush, double fontSize)
    {
        var block = new TextBlock { Text = text, Foreground = brush, FontSize = fontSize };
        Canvas.SetLeft(block, x);
        Canvas.SetTop(block, y);
        canvas.Children.Add(block);
    }

    private static void AddLegend(Canvas canvas, double x, double y, Brush bar, Brush max, Brush trend, Brush label)
    {
        var cursor = x;
        void Item(Brush swatch, string text)
        {
            var rect = new Rectangle { Width = 10, Height = 10, Fill = swatch, RadiusX = 2, RadiusY = 2 };
            Canvas.SetLeft(rect, cursor);
            Canvas.SetTop(rect, y + 2);
            canvas.Children.Add(rect);
            AddText(canvas, text, cursor + 14, y, label, 10);
            cursor += 14 + text.Length * 6.5 + 14;
        }

        Item(bar, "Online");
        Item(max, "Max");
        Item(trend, "Trend");
    }
}

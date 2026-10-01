using OxyPlot;
using OxyPlot.Axes;
using OxyPlot.Series;

namespace openXRTK_Graph;

public static class ChartBuilder
{
    // Common framerate lines to annotate (in ms = 1000/Hz)
    private static readonly (string Label, double Ms)[] FramerateMarkers =
    [
        ("144Hz", 1000.0 / 144),
        ("120Hz", 1000.0 / 120),
        ("90Hz",  1000.0 / 90),
        ("80Hz",  1000.0 / 80),
        ("72Hz",  1000.0 / 72),
    ];

    private static OxyColor Dark => OxyColor.FromRgb(20, 20, 20);
    private static OxyColor GridColor => OxyColor.FromRgb(50, 50, 50);
    private static OxyColor TextColor => OxyColor.FromRgb(200, 200, 200);

    // Session comparison color palettes
    // Session A — warm/orange family (solid lines)
    internal static readonly OxyColor ColA_AppGpu    = OxyColor.FromRgb(255, 140,  50);
    internal static readonly OxyColor ColA_AppCpu    = OxyColor.FromRgb(255, 100,  80);
    internal static readonly OxyColor ColA_RenderCpu = OxyColor.FromRgb(255, 210,  70);
    internal static readonly OxyColor ColA_Fps       = OxyColor.FromRgb(255, 230,  80);
    // Session B — cool/blue family (dashed lines)
    internal static readonly OxyColor ColB_AppGpu    = OxyColor.FromRgb(120, 160, 255);
    internal static readonly OxyColor ColB_AppCpu    = OxyColor.FromRgb( 72, 209, 200);
    internal static readonly OxyColor ColB_RenderCpu = OxyColor.FromRgb(160, 130, 255);
    internal static readonly OxyColor ColB_Fps       = OxyColor.FromRgb(  0, 220, 255);

    private static PlotModel CreateDarkModel(string title)
    {
        var model = new PlotModel
        {
            Title = title,
            Background = Dark,
            PlotAreaBackground = Dark,
            TextColor = TextColor,
            TitleColor = TextColor,
            PlotAreaBorderColor = GridColor,
            TitleFontSize = 13,
        };
        return model;
    }

    private static LinearAxis CreateLinearAxis(AxisPosition position, string title, OxyColor? textColor = null)
    {
        return new LinearAxis
        {
            Position = position,
            Title = title,
            TitleColor = textColor ?? TextColor,
            TextColor = textColor ?? TextColor,
            TicklineColor = GridColor,
            MajorGridlineStyle = LineStyle.Solid,
            MajorGridlineColor = GridColor,
            MinorGridlineStyle = LineStyle.None,
            AxislineColor = GridColor,
        };
    }

    /// <summary>
    /// Builds a frametime distribution histogram (percentage of frames per frametime bucket).
    /// </summary>
    public static PlotModel BuildFrametimeDistribution(
        IList<CsvDataPoint> data,
        string title,
        double bucketWidthMs = 0.2)
    {
        var model = CreateDarkModel(title);
        model.Legends.Add(new OxyPlot.Legends.Legend
        {
            LegendPosition = OxyPlot.Legends.LegendPosition.TopRight,
            LegendTextColor = TextColor,
            LegendBackground = Dark,
            LegendBorderThickness = 0,
        });

        var xAxis = CreateLinearAxis(AxisPosition.Bottom, "Frametime (ms)");
        xAxis.Minimum = 0;
        xAxis.Maximum = 30;
        model.Axes.Add(xAxis);

        var yAxis = CreateLinearAxis(AxisPosition.Left, "Percentage (%)");
        yAxis.Minimum = 0;
        model.Axes.Add(yAxis);

        AddFramerateMarkers(model, xAxis);

        // App CPU series
        AddDistributionSeries(model, data.Select(d => d.AppCpuMs).ToList(), bucketWidthMs,
            "App CPU", OxyColor.FromRgb(255, 100, 100));

        // Render CPU series
        AddDistributionSeries(model, data.Select(d => d.RenderCpuMs).ToList(), bucketWidthMs,
            "Render CPU", OxyColor.FromRgb(100, 220, 100));

        // App GPU series
        AddDistributionSeries(model, data.Select(d => d.AppGpuMs).ToList(), bucketWidthMs,
            "App GPU", OxyColor.FromRgb(100, 160, 255));

        return model;
    }

    private static void AddDistributionSeries(PlotModel model, IList<double> values, double bucketWidthMs, string title, OxyColor color, LineStyle lineStyle = LineStyle.Solid)
    {
        if (values.Count == 0) return;

        double maxVal = Math.Min(values.Max(), 30.0);
        int buckets = (int)Math.Ceiling(maxVal / bucketWidthMs) + 1;

        var counts = new int[buckets];
        foreach (var v in values)
        {
            int idx = (int)(v / bucketWidthMs);
            if (idx >= 0 && idx < buckets)
                counts[idx]++;
        }

        double total = values.Count;
        var series = new LineSeries
        {
            Title = title,
            Color = color,
            StrokeThickness = 1.5,
            MarkerType = MarkerType.None,
            LineStyle = lineStyle,
        };

        for (int i = 0; i < buckets; i++)
        {
            double ms = i * bucketWidthMs;
            double pct = counts[i] / total * 100.0;
            series.Points.Add(new DataPoint(ms, pct));
        }

        model.Series.Add(series);
    }

    private static void AddFramerateMarkers(PlotModel model, LinearAxis xAxis)
    {
        foreach (var (label, ms) in FramerateMarkers)
        {
            model.Annotations.Add(new OxyPlot.Annotations.LineAnnotation
            {
                Type = OxyPlot.Annotations.LineAnnotationType.Vertical,
                X = ms,
                Color = OxyColor.FromArgb(160, 180, 180, 180),
                StrokeThickness = 1,
                LineStyle = LineStyle.Solid,
                Text = label,
                TextColor = OxyColor.FromRgb(180, 180, 180),
                FontSize = 10,
                TextVerticalAlignment = VerticalAlignment.Top,
            });
        }
    }

    /// <summary>
    /// Builds a time-series plot for FPS over time.
    /// </summary>
    public static PlotModel BuildFpsTimeSeries(IList<CsvDataPoint> data)
    {
        var model = CreateDarkModel("FPS over Time");
        model.Legends.Add(new OxyPlot.Legends.Legend
        {
            LegendPosition = OxyPlot.Legends.LegendPosition.TopRight,
            LegendTextColor = TextColor,
            LegendBackground = Dark,
            LegendBorderThickness = 0,
        });

        var xAxis = new DateTimeAxis
        {
            Position = AxisPosition.Bottom,
            Title = "Time",
            StringFormat = "HH:mm:ss",
            TitleColor = TextColor,
            TextColor = TextColor,
            TicklineColor = GridColor,
            MajorGridlineStyle = LineStyle.Solid,
            MajorGridlineColor = GridColor,
            MinorGridlineStyle = LineStyle.None,
            AxislineColor = GridColor,
        };
        model.Axes.Add(xAxis);
        model.Axes.Add(CreateLinearAxis(AxisPosition.Left, "FPS"));

        var fps = new LineSeries { Title = "FPS", Color = OxyColor.FromRgb(255, 200, 50), StrokeThickness = 1.2, MarkerType = MarkerType.None };
        foreach (var d in data)
            fps.Points.Add(new DataPoint(DateTimeAxis.ToDouble(d.Time), d.Fps));
        model.Series.Add(fps);

        return model;
    }

    /// <summary>
    /// Builds a time-series plot for CPU/GPU frametimes in ms.
    /// </summary>
    public static PlotModel BuildCpuGpuTimeSeries(IList<CsvDataPoint> data)
    {
        var model = CreateDarkModel("CPU / GPU Frametime over Time");
        model.Legends.Add(new OxyPlot.Legends.Legend
        {
            LegendPosition = OxyPlot.Legends.LegendPosition.TopRight,
            LegendTextColor = TextColor,
            LegendBackground = Dark,
            LegendBorderThickness = 0,
        });

        var xAxis = new DateTimeAxis
        {
            Position = AxisPosition.Bottom,
            Title = "Time",
            StringFormat = "HH:mm:ss",
            TitleColor = TextColor,
            TextColor = TextColor,
            TicklineColor = GridColor,
            MajorGridlineStyle = LineStyle.Solid,
            MajorGridlineColor = GridColor,
            MinorGridlineStyle = LineStyle.None,
            AxislineColor = GridColor,
        };
        model.Axes.Add(xAxis);
        model.Axes.Add(CreateLinearAxis(AxisPosition.Left, "Frametime (ms)"));

        var appCpu = new LineSeries { Title = "App CPU", Color = OxyColor.FromRgb(255, 100, 100), StrokeThickness = 1.2, MarkerType = MarkerType.None };
        var renderCpu = new LineSeries { Title = "Render CPU", Color = OxyColor.FromRgb(100, 220, 100), StrokeThickness = 1.2, MarkerType = MarkerType.None };
        var appGpu = new LineSeries { Title = "App GPU", Color = OxyColor.FromRgb(100, 160, 255), StrokeThickness = 1.2, MarkerType = MarkerType.None };

        foreach (var d in data)
        {
            double t = DateTimeAxis.ToDouble(d.Time);
            appCpu.Points.Add(new DataPoint(t, d.AppCpuMs));
            renderCpu.Points.Add(new DataPoint(t, d.RenderCpuMs));
            appGpu.Points.Add(new DataPoint(t, d.AppGpuMs));
        }

        model.Series.Add(appCpu);
        model.Series.Add(renderCpu);
        model.Series.Add(appGpu);

        return model;
    }

    /// <summary>
    /// Builds a combined overlay: CPU/GPU frametimes (left axis) + FPS (right axis) on a shared time axis.
    /// </summary>
    public static PlotModel BuildFpsAndFrametimeOverlay(IList<CsvDataPoint> data)
    {
        var model = CreateDarkModel("FPS & Frametimes over Time");
        model.Legends.Add(new OxyPlot.Legends.Legend
        {
            LegendPosition = OxyPlot.Legends.LegendPosition.TopRight,
            LegendTextColor = TextColor,
            LegendBackground = Dark,
            LegendBorderThickness = 0,
        });

        var xAxis = new DateTimeAxis
        {
            Position = AxisPosition.Bottom,
            Title = "Time",
            StringFormat = "HH:mm:ss",
            TitleColor = TextColor,
            TextColor = TextColor,
            TicklineColor = GridColor,
            MajorGridlineStyle = LineStyle.Solid,
            MajorGridlineColor = GridColor,
            MinorGridlineStyle = LineStyle.None,
            AxislineColor = GridColor,
        };

        var yFrametime = CreateLinearAxis(AxisPosition.Left, "Frametime (ms)");
        yFrametime.Key = "ft";

        var yFps = new LinearAxis
        {
            Position = AxisPosition.Right,
            Title = "FPS",
            TitleColor = OxyColor.FromRgb(255, 200, 50),
            TextColor = OxyColor.FromRgb(255, 200, 50),
            TicklineColor = GridColor,
            MajorGridlineStyle = LineStyle.None,
            AxislineColor = GridColor,
            Key = "fps",
        };

        model.Axes.Add(xAxis);
        model.Axes.Add(yFrametime);
        model.Axes.Add(yFps);

        var appCpu    = new LineSeries { Title = "App CPU",    Color = OxyColor.FromRgb(255, 100, 100), StrokeThickness = 1.2, MarkerType = MarkerType.None, YAxisKey = "ft" };
        var renderCpu = new LineSeries { Title = "Render CPU", Color = OxyColor.FromRgb(100, 220, 100), StrokeThickness = 1.2, MarkerType = MarkerType.None, YAxisKey = "ft" };
        var appGpu    = new LineSeries { Title = "App GPU",    Color = OxyColor.FromRgb(100, 160, 255), StrokeThickness = 1.2, MarkerType = MarkerType.None, YAxisKey = "ft" };
        var fps       = new LineSeries { Title = "FPS",        Color = OxyColor.FromRgb(255, 200, 50),  StrokeThickness = 1.5, MarkerType = MarkerType.None, YAxisKey = "fps", LineStyle = LineStyle.Dash };

        foreach (var d in data)
        {
            double t = DateTimeAxis.ToDouble(d.Time);
            appCpu.Points.Add(new DataPoint(t, d.AppCpuMs));
            renderCpu.Points.Add(new DataPoint(t, d.RenderCpuMs));
            appGpu.Points.Add(new DataPoint(t, d.AppGpuMs));
            fps.Points.Add(new DataPoint(t, d.Fps));
        }

        model.Series.Add(appCpu);
        model.Series.Add(renderCpu);
        model.Series.Add(appGpu);
        model.Series.Add(fps);

        return model;
    }

    /// <summary>
    /// Overlaid frametime distribution: Session A (solid) vs Session B (dashed).
    /// </summary>
    public static PlotModel BuildComparisonDistribution(
        IList<CsvDataPoint> dataA, string labelA,
        IList<CsvDataPoint> dataB, string labelB,
        double bucketWidthMs = 0.2)
    {
        var model = CreateDarkModel("Frametime Distribution — A vs. B");
        model.Legends.Add(new OxyPlot.Legends.Legend
        {
            LegendPosition = OxyPlot.Legends.LegendPosition.TopRight,
            LegendTextColor = TextColor, LegendBackground = Dark, LegendBorderThickness = 0,
        });

        var xAxis = CreateLinearAxis(AxisPosition.Bottom, "Frametime (ms)");
        xAxis.Minimum = 0; xAxis.Maximum = 30;
        model.Axes.Add(xAxis);
        var yAxis = CreateLinearAxis(AxisPosition.Left, "Percentage (%)");
        yAxis.Minimum = 0;
        model.Axes.Add(yAxis);
        AddFramerateMarkers(model, xAxis);

        AddDistributionSeries(model, dataA.Select(d => d.AppCpuMs).ToList(),    bucketWidthMs, "A: App CPU",    ColA_AppCpu,    LineStyle.Solid);
        AddDistributionSeries(model, dataA.Select(d => d.RenderCpuMs).ToList(), bucketWidthMs, "A: Render CPU", ColA_RenderCpu, LineStyle.Solid);
        AddDistributionSeries(model, dataA.Select(d => d.AppGpuMs).ToList(),    bucketWidthMs, "A: App GPU",    ColA_AppGpu,    LineStyle.Solid);
        AddDistributionSeries(model, dataB.Select(d => d.AppCpuMs).ToList(),    bucketWidthMs, "B: App CPU",    ColB_AppCpu,    LineStyle.Dash);
        AddDistributionSeries(model, dataB.Select(d => d.RenderCpuMs).ToList(), bucketWidthMs, "B: Render CPU", ColB_RenderCpu, LineStyle.Dash);
        AddDistributionSeries(model, dataB.Select(d => d.AppGpuMs).ToList(),    bucketWidthMs, "B: App GPU",    ColB_AppGpu,    LineStyle.Dash);

        return model;
    }

    /// <summary>
    /// Overlaid GPU frametime + FPS for two sessions, X-axis normalized to frame index.
    /// Session A: solid lines. Session B: dashed lines.
    /// </summary>
    public static PlotModel BuildComparisonFrameIndexSeries(
        IList<CsvDataPoint> dataA, string labelA,
        IList<CsvDataPoint> dataB, string labelB)
    {
        var model = CreateDarkModel("GPU Frametime & FPS by Frame — A vs. B");
        model.Legends.Add(new OxyPlot.Legends.Legend
        {
            LegendPosition = OxyPlot.Legends.LegendPosition.TopRight,
            LegendTextColor = TextColor, LegendBackground = Dark, LegendBorderThickness = 0,
        });

        var xAxis = CreateLinearAxis(AxisPosition.Bottom, "Frame Index");
        var yFt   = CreateLinearAxis(AxisPosition.Left, "GPU Frametime (ms)");
        yFt.Key   = "ft";
        var yFps  = new LinearAxis
        {
            Position = AxisPosition.Right, Title = "FPS",
            TitleColor = TextColor, TextColor = TextColor,
            TicklineColor = GridColor, MajorGridlineStyle = LineStyle.None,
            AxislineColor = GridColor, Key = "fps",
        };
        model.Axes.Add(xAxis);
        model.Axes.Add(yFt);
        model.Axes.Add(yFps);

        var gpuA = new LineSeries { Title = "A: App GPU", Color = ColA_AppGpu, StrokeThickness = 1.3, MarkerType = MarkerType.None, YAxisKey = "ft",  LineStyle = LineStyle.Solid };
        var fpsA = new LineSeries { Title = "A: FPS",     Color = ColA_Fps,    StrokeThickness = 1.3, MarkerType = MarkerType.None, YAxisKey = "fps", LineStyle = LineStyle.Solid };
        for (int i = 0; i < dataA.Count; i++)
        {
            gpuA.Points.Add(new DataPoint(i, dataA[i].AppGpuMs));
            fpsA.Points.Add(new DataPoint(i, dataA[i].Fps));
        }

        var gpuB = new LineSeries { Title = "B: App GPU", Color = ColB_AppGpu, StrokeThickness = 1.3, MarkerType = MarkerType.None, YAxisKey = "ft",  LineStyle = LineStyle.Dash };
        var fpsB = new LineSeries { Title = "B: FPS",     Color = ColB_Fps,    StrokeThickness = 1.3, MarkerType = MarkerType.None, YAxisKey = "fps", LineStyle = LineStyle.Dash };
        for (int i = 0; i < dataB.Count; i++)
        {
            gpuB.Points.Add(new DataPoint(i, dataB[i].AppGpuMs));
            fpsB.Points.Add(new DataPoint(i, dataB[i].Fps));
        }

        model.Series.Add(gpuA); model.Series.Add(fpsA);
        model.Series.Add(gpuB); model.Series.Add(fpsB);
        return model;
    }

    /// <summary>
    /// Builds a time-series plot for VRAM usage.
    /// </summary>
    public static PlotModel BuildVramTimeSeries(IList<CsvDataPoint> data)
    {
        var model = CreateDarkModel("VRAM Usage over Time");
        model.Legends.Add(new OxyPlot.Legends.Legend
        {
            LegendPosition = OxyPlot.Legends.LegendPosition.TopRight,
            LegendTextColor = TextColor,
            LegendBackground = Dark,
            LegendBorderThickness = 0,
        });

        var xAxis = new DateTimeAxis
        {
            Position = AxisPosition.Bottom,
            Title = "Time",
            StringFormat = "HH:mm:ss",
            TitleColor = TextColor,
            TextColor = TextColor,
            TicklineColor = GridColor,
            MajorGridlineStyle = LineStyle.Solid,
            MajorGridlineColor = GridColor,
            MinorGridlineStyle = LineStyle.None,
            AxislineColor = GridColor,
        };
        var yMb = CreateLinearAxis(AxisPosition.Left, "VRAM (MB)");
        var yPct = new LinearAxis
        {
            Position = AxisPosition.Right,
            Title = "VRAM (%)",
            TitleColor = OxyColor.FromRgb(255, 180, 80),
            TextColor = OxyColor.FromRgb(255, 180, 80),
            TicklineColor = GridColor,
            MajorGridlineStyle = LineStyle.None,
            AxislineColor = GridColor,
            Key = "pct"
        };

        model.Axes.Add(xAxis);
        model.Axes.Add(yMb);
        model.Axes.Add(yPct);

        var vramMb = new LineSeries { Title = "VRAM (MB)", Color = OxyColor.FromRgb(200, 120, 255), StrokeThickness = 1.2, MarkerType = MarkerType.None };
        var vramPct = new LineSeries { Title = "VRAM (%)", Color = OxyColor.FromRgb(255, 180, 80), StrokeThickness = 1.2, MarkerType = MarkerType.None, YAxisKey = "pct" };

        foreach (var d in data)
        {
            double t = DateTimeAxis.ToDouble(d.Time);
            vramMb.Points.Add(new DataPoint(t, d.VramMb));
            vramPct.Points.Add(new DataPoint(t, d.VramPercent));
        }

        model.Series.Add(vramMb);
        model.Series.Add(vramPct);

        return model;
    }
}

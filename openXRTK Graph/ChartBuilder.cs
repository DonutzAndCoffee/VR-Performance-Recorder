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
    // Session C — green family (dotted lines)
    internal static readonly OxyColor ColC_AppGpu    = OxyColor.FromRgb( 90, 220,  90);
    internal static readonly OxyColor ColC_AppCpu    = OxyColor.FromRgb(190, 240,  90);
    internal static readonly OxyColor ColC_RenderCpu = OxyColor.FromRgb( 60, 180, 120);
    internal static readonly OxyColor ColC_Fps       = OxyColor.FromRgb(150, 255, 150);

    private static (OxyColor AppGpu, OxyColor AppCpu, OxyColor RenderCpu, OxyColor Fps, LineStyle Style) SessionStyle(int index) => index switch
    {
        0 => (ColA_AppGpu, ColA_AppCpu, ColA_RenderCpu, ColA_Fps, LineStyle.Solid),
        1 => (ColB_AppGpu, ColB_AppCpu, ColB_RenderCpu, ColB_Fps, LineStyle.Dash),
        _ => (ColC_AppGpu, ColC_AppCpu, ColC_RenderCpu, ColC_Fps, LineStyle.Dot),
    };

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
        double bucketWidthMs = 0.2,
        bool includeCpu = true,
        bool includeGpu = true)
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
        xAxis.AbsoluteMinimum = 0;
        xAxis.AbsoluteMaximum = 30;
        model.Axes.Add(xAxis);

        var yAxis = CreateLinearAxis(AxisPosition.Left, "Percentage (%)");
        yAxis.Minimum = 0;
        yAxis.AbsoluteMinimum = 0;
        model.Axes.Add(yAxis);

        AddFramerateMarkers(model, xAxis);

        // Samples with 0 ms are treated as "not measured" and excluded from the distribution
        if (includeCpu)
        {
            AddDistributionSeries(model, data.Select(d => d.AppCpuMs).Where(v => v > 0).ToList(), bucketWidthMs,
                "App CPU", OxyColor.FromRgb(255, 100, 100));

            AddDistributionSeries(model, data.Select(d => d.RenderCpuMs).Where(v => v > 0).ToList(), bucketWidthMs,
                "Render CPU", OxyColor.FromRgb(100, 220, 100));
        }

        if (includeGpu)
        {
            AddDistributionSeries(model, data.Select(d => d.AppGpuMs).Where(v => v > 0).ToList(), bucketWidthMs,
                "App GPU", OxyColor.FromRgb(100, 160, 255));
        }

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
    /// Overlaid frametime distribution: Session A (solid), B (dashed), C (dotted).
    /// </summary>
    public static PlotModel BuildComparisonDistribution(
        IList<CompareSession> sessions,
        double bucketWidthMs = 0.2)
    {
        var model = CreateDarkModel("Frametime Distribution — " + string.Join(" vs. ", sessions.Select(s => s.Key)));
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

        for (int i = 0; i < sessions.Count; i++)
        {
            var s = sessions[i];
            var st = SessionStyle("ABC".IndexOf(s.Key, StringComparison.Ordinal));
            AddDistributionSeries(model, s.Data.Select(d => d.AppCpuMs).Where(v => v > 0).ToList(),    bucketWidthMs, $"{s.Key}: App CPU",    st.AppCpu,    st.Style);
            AddDistributionSeries(model, s.Data.Select(d => d.RenderCpuMs).Where(v => v > 0).ToList(), bucketWidthMs, $"{s.Key}: Render CPU", st.RenderCpu, st.Style);
            AddDistributionSeries(model, s.Data.Select(d => d.AppGpuMs).Where(v => v > 0).ToList(),    bucketWidthMs, $"{s.Key}: App GPU",    st.AppGpu,    st.Style);
        }

        return model;
    }

    /// <summary>
    /// Overlaid GPU frametime + FPS for up to three sessions, X-axis normalized to frame index.
    /// Session A: solid lines. Session B: dashed lines. Session C: dotted lines.
    /// </summary>
    public static PlotModel BuildComparisonFrameIndexSeries(IList<CompareSession> sessions)
    {
        var model = CreateDarkModel("GPU Frametime & FPS by Frame — " + string.Join(" vs. ", sessions.Select(s => s.Key)));
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

        for (int s = 0; s < sessions.Count; s++)
        {
            var data = sessions[s].Data;
            var st = SessionStyle("ABC".IndexOf(sessions[s].Key, StringComparison.Ordinal));
            var gpu = new LineSeries { Title = $"{sessions[s].Key}: App GPU", Color = st.AppGpu, StrokeThickness = 1.3, MarkerType = MarkerType.None, YAxisKey = "ft",  LineStyle = st.Style };
            var fps = new LineSeries { Title = $"{sessions[s].Key}: FPS",     Color = st.Fps,    StrokeThickness = 1.3, MarkerType = MarkerType.None, YAxisKey = "fps", LineStyle = st.Style };
            for (int i = 0; i < data.Count; i++)
            {
                gpu.Points.Add(new DataPoint(i, data[i].AppGpuMs));
                fps.Points.Add(new DataPoint(i, data[i].Fps));
            }
            model.Series.Add(gpu); model.Series.Add(fps);
        }
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

namespace openXRTK_Graph;

public class CsvDataPoint
{
    public DateTime Time { get; set; }
    public double Fps { get; set; }
    public double AppCpuUs { get; set; }
    public double RenderCpuUs { get; set; }
    public double AppGpuUs { get; set; }
    public double VramMb { get; set; }
    public double VramPercent { get; set; }

    public double AppCpuMs => AppCpuUs / 1000.0;
    public double RenderCpuMs => RenderCpuUs / 1000.0;
    public double AppGpuMs => AppGpuUs / 1000.0;
}

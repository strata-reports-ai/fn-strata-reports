namespace StrataReports.Functions.Services;

public sealed class ReportRenderException : Exception
{
    public ReportRenderException(string message) : base(message) { }
    public ReportRenderException(string message, Exception inner) : base(message, inner) { }
}

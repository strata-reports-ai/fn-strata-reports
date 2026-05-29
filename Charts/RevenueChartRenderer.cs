using ScottPlot;
using StrataReports.Functions.Services;

namespace StrataReports.Functions.Charts;

public static class RevenueChartRenderer
{
    public static byte[] RenderMonthlyRevenueBars(IReadOnlyList<MonthlyRevenueDto> months)
    {
        Plot plot = new();
        plot.FigureBackground.Color = Colors.White;

        double[] values = months.Select(m => (double)m.Total).ToArray();
        double[] positions = Enumerable.Range(0, values.Length).Select(i => (double)i).ToArray();
        string[] labels = months.Select(m => $"{m.Year}-{m.Month:D2}").ToArray();

        ScottPlot.Bar[] bars = values.Select((v, i) => new ScottPlot.Bar
        {
            Position = positions[i],
            Value = v,
            FillColor = Color.FromHex("#4F81BD"),
        }).ToArray();

        plot.Add.Bars(bars);
        plot.Axes.Bottom.TickGenerator = new ScottPlot.TickGenerators.NumericManual(
            positions, labels);
        plot.Axes.Bottom.TickLabelStyle.Rotation = -45;
        plot.Axes.Left.Label.Text = "Gross Revenue ($)";
        plot.Title("Monthly Gross Revenue");
        plot.Layout.Frameless();

        return plot.GetImageBytes(700, 350, ImageFormat.Png);
    }

    public static byte[] RenderOccupancyAdrOverlay(
        IReadOnlyList<MonthlyRevenueDto> months,
        decimal occupancyRate,
        decimal adr)
    {
        Plot plot = new();
        plot.FigureBackground.Color = Colors.White;

        double[] positions = Enumerable.Range(0, months.Count).Select(i => (double)i).ToArray();
        string[] labels = months.Select(m => $"{m.Year}-{m.Month:D2}").ToArray();

        double[] occValues = months.Select(m =>
        {
            int daysInMonth = DateTime.DaysInMonth(m.Year, m.Month);
            return daysInMonth > 0 ? (double)m.Nights / daysInMonth * 100.0 : 0.0;
        }).ToArray();

        double[] adrValues = months.Select(m =>
            m.Nights > 0 ? (double)m.Total / m.Nights : 0.0).ToArray();

        ScottPlot.Plottables.Scatter occLine = plot.Add.Scatter(positions, occValues);
        occLine.Color = Color.FromHex("#4F81BD");
        occLine.LineWidth = 2;
        occLine.MarkerSize = 6;
        occLine.LegendText = "Occupancy %";

        ScottPlot.IYAxis rightAxis = plot.Axes.AddRightAxis();
        ScottPlot.Plottables.Scatter adrLine = plot.Add.Scatter(positions, adrValues);
        adrLine.Axes.YAxis = rightAxis;
        adrLine.Color = Color.FromHex("#C0504D");
        adrLine.LineWidth = 2;
        adrLine.MarkerSize = 6;
        adrLine.LegendText = "ADR ($)";

        plot.Axes.Bottom.TickGenerator = new ScottPlot.TickGenerators.NumericManual(
            positions, labels);
        plot.Axes.Bottom.TickLabelStyle.Rotation = -45;
        plot.Axes.Left.Label.Text = "Occupancy (%)";
        rightAxis.Label.Text = "ADR ($)";
        plot.Title("Occupancy Rate vs ADR");
        plot.ShowLegend();
        plot.Layout.Frameless();

        return plot.GetImageBytes(700, 350, ImageFormat.Png);
    }
}

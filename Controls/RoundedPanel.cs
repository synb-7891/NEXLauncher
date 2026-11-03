using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace NEXLauncher.Controls;

/// <summary>
/// 带圆角背景与细边框的 StackPanel。
/// WPF 的 Panel 只支持直角 Background，Border 又只能放一个子元素，
/// 这里直接在 OnRender 里画圆角矩形，既能圆角又保留 Children 的用法。
/// </summary>
public sealed class RoundedPanel : StackPanel
{
    public static readonly DependencyProperty RadiusProperty = DependencyProperty.Register(
        nameof(Radius), typeof(double), typeof(RoundedPanel),
        new FrameworkPropertyMetadata(14.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty StrokeProperty = DependencyProperty.Register(
        nameof(Stroke), typeof(Brush), typeof(RoundedPanel),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty StrokeThicknessProperty = DependencyProperty.Register(
        nameof(StrokeThickness), typeof(double), typeof(RoundedPanel),
        new FrameworkPropertyMetadata(1.0, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>圆角半径（px）。</summary>
    public double Radius
    {
        get => (double)GetValue(RadiusProperty);
        set => SetValue(RadiusProperty, value);
    }

    /// <summary>边框画刷，null 则不画边框。</summary>
    public Brush? Stroke
    {
        get => (Brush?)GetValue(StrokeProperty);
        set => SetValue(StrokeProperty, value);
    }

    /// <summary>边框线宽（px），0 表示不画。</summary>
    public double StrokeThickness
    {
        get => (double)GetValue(StrokeThicknessProperty);
        set => SetValue(StrokeThicknessProperty, value);
    }

    protected override void OnRender(DrawingContext dc)
    {
        // 不调用 base：Panel 默认会把 Background 画成直角矩形
        var w = ActualWidth;
        var h = ActualHeight;
        if (w <= 0 || h <= 0) return;

        var radius = Math.Max(0, Math.Min(Radius, Math.Min(w, h) / 2));
        var thickness = StrokeThickness;
        var rect = new Rect(0, 0, w, h);
        Pen? pen = null;

        if (thickness > 0 && Stroke != null)
        {
            pen = new Pen(Stroke, thickness);
            pen.Freeze();
            // 内缩半个线宽，避免描边被布局边界裁掉
            rect = new Rect(thickness / 2, thickness / 2,
                Math.Max(0, w - thickness), Math.Max(0, h - thickness));
        }

        dc.DrawRoundedRectangle(Background, pen, rect, radius, radius);
    }
}

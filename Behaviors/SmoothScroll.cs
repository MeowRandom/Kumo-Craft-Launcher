using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace Kumo.Behaviors;

public static class SmoothScroll
{
    private const double NativeWheelLineHeight = 16;
    private const double SpringFrequency = 20;
    private static readonly ConditionalWeakTable<ScrollViewer, ScrollState> States = [];

    public static readonly DependencyProperty IsEnabledProperty = DependencyProperty.RegisterAttached(
        "IsEnabled",
        typeof(bool),
        typeof(SmoothScroll),
        new PropertyMetadata(false, OnIsEnabledChanged));

    public static readonly DependencyProperty WheelFactorProperty = DependencyProperty.RegisterAttached(
        "WheelFactor",
        typeof(double),
        typeof(SmoothScroll),
        new PropertyMetadata(1d));

    public static readonly DependencyProperty SpringFrequencyProperty = DependencyProperty.RegisterAttached(
        "SpringFrequency",
        typeof(double),
        typeof(SmoothScroll),
        new PropertyMetadata(SpringFrequency));

    private static readonly DependencyProperty IsHookedProperty = DependencyProperty.RegisterAttached(
        "IsHooked",
        typeof(bool),
        typeof(SmoothScroll),
        new PropertyMetadata(false));

    public static void SetIsEnabled(DependencyObject element, bool value) =>
        element.SetValue(IsEnabledProperty, value);

    public static bool GetIsEnabled(DependencyObject element) =>
        (bool)element.GetValue(IsEnabledProperty);

    public static void SetWheelFactor(DependencyObject element, double value) =>
        element.SetValue(WheelFactorProperty, value);

    public static double GetWheelFactor(DependencyObject element) =>
        (double)element.GetValue(WheelFactorProperty);

    public static void SetSpringFrequency(DependencyObject element, double value) =>
        element.SetValue(SpringFrequencyProperty, value);

    public static double GetSpringFrequency(DependencyObject element) =>
        Math.Clamp((double)element.GetValue(SpringFrequencyProperty), 5, 30);

    private static void OnIsEnabledChanged(DependencyObject element, DependencyPropertyChangedEventArgs e)
    {
        if (element is not FrameworkElement owner || e.NewValue is not true) return;

        RoutedEventHandler? loadedHandler = null;
        DependencyPropertyChangedEventHandler? visibilityHandler = null;

        void Cleanup()
        {
            if (loadedHandler is not null) owner.Loaded -= loadedHandler;
            if (visibilityHandler is not null) owner.IsVisibleChanged -= visibilityHandler;
        }

        if (owner.IsLoaded)
        {
            TryAttach(owner);
            return;
        }

        loadedHandler = (_, _) =>
        {
            if (TryAttach(owner)) Cleanup();
        };
        visibilityHandler = (_, _) =>
        {
            if (owner.IsVisible && TryAttach(owner)) Cleanup();
        };
        owner.Loaded += loadedHandler;
        owner.IsVisibleChanged += visibilityHandler;
    }

    private static bool TryAttach(FrameworkElement owner)
    {
        if (!GetIsEnabled(owner)) return false;
        if (owner is Control control) control.ApplyTemplate();
        var viewer = owner as ScrollViewer ?? FindScrollViewer(owner);
        if (viewer is null) return false;
        if (!ReferenceEquals(owner, viewer))
        {
            SetWheelFactor(viewer, GetWheelFactor(owner));
            SetSpringFrequency(viewer, GetSpringFrequency(owner));
        }
        Attach(viewer);
        return true;
    }

    private static void Attach(ScrollViewer viewer)
    {
        if ((bool)viewer.GetValue(IsHookedProperty)) return;
        viewer.SetValue(IsHookedProperty, true);
        viewer.PreviewMouseWheel += OnPreviewMouseWheel;
        viewer.Unloaded += OnViewerUnloaded;
    }

    private static void OnViewerUnloaded(object sender, RoutedEventArgs e)
    {
        if (sender is not ScrollViewer viewer) return;
        if (States.TryGetValue(viewer, out var state)) StopAnimation(state);
    }

    private static void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is not ScrollViewer viewer) return;
        if (HasInnerScrollingControl(e.OriginalSource as DependencyObject, viewer)) return;
        if (ScrollByWheel(viewer, e.Delta)) e.Handled = true;
    }

    public static bool ScrollByWheel(ScrollViewer viewer, int delta)
    {
        if (delta == 0 || viewer.ScrollableHeight <= 0) return false;
        var state = States.GetOrCreateValue(viewer);
        if (!state.IsAnimating) state.TargetOffset = viewer.VerticalOffset;
        if (delta > 0 && state.TargetOffset <= 0.5)
        {
            state.TargetOffset = 0;
            StartAnimation(viewer, state);
            return true;
        }
        if (delta < 0 && state.TargetOffset >= viewer.ScrollableHeight - 0.5)
        {
            state.TargetOffset = viewer.ScrollableHeight;
            StartAnimation(viewer, state);
            return true;
        }

        var wheelNotches = Math.Clamp(delta / 120d, -3, 3);
        var wheelLines = SystemParameters.WheelScrollLines;
        if (wheelLines == 0) return true;
        var wheelStep = wheelLines < 0
            ? Math.Max(1, viewer.ViewportHeight - 24)
            : wheelLines * NativeWheelLineHeight;
        wheelStep *= Math.Clamp(GetWheelFactor(viewer), 0.1, 2);
        state.TargetOffset = Math.Clamp(
            state.TargetOffset - wheelNotches * wheelStep,
            0,
            viewer.ScrollableHeight);

        StartAnimation(viewer, state);
        return true;
    }

    private static void StartAnimation(ScrollViewer viewer, ScrollState state)
    {
        if (state.IsAnimating) return;
        state.IsAnimating = true;
        state.Velocity = 0;
        state.LastRenderingSeconds = -1;
        state.RenderHandler ??= (_, args) => RenderFrame(viewer, state, args);
        CompositionTarget.Rendering += state.RenderHandler;
    }

    private static void RenderFrame(ScrollViewer viewer, ScrollState state, EventArgs eventArgs)
    {
        if (eventArgs is not RenderingEventArgs rendering) return;
        var now = rendering.RenderingTime.TotalSeconds;
        if (state.LastRenderingSeconds < 0)
        {
            state.LastRenderingSeconds = now;
            return;
        }

        var elapsed = Math.Clamp(now - state.LastRenderingSeconds, 0, 0.05);
        state.LastRenderingSeconds = now;
        var maximum = viewer.ScrollableHeight;
        var target = Math.Clamp(state.TargetOffset, 0, maximum);
        var current = viewer.VerticalOffset;
        var displacement = current - target;
        if (Math.Abs(displacement) < 0.2 && Math.Abs(state.Velocity) < 2)
        {
            viewer.ScrollToVerticalOffset(target);
            StopAnimation(state);
            return;
        }

        var frequency = GetSpringFrequency(viewer);
        var decay = Math.Exp(-frequency * elapsed);
        var velocityTerm = (state.Velocity + frequency * displacement) * elapsed;
        var nextDisplacement = (displacement + velocityTerm) * decay;
        state.Velocity = (state.Velocity - frequency * velocityTerm) * decay;
        viewer.ScrollToVerticalOffset(target + nextDisplacement);
    }

    private static void StopAnimation(ScrollState state)
    {
        state.IsAnimating = false;
        state.LastRenderingSeconds = -1;
        state.Velocity = 0;
        if (state.RenderHandler is not null)
            CompositionTarget.Rendering -= state.RenderHandler;
    }

    private static bool HasInnerScrollingControl(DependencyObject? source, ScrollViewer owner)
    {
        while (source is not null && source != owner)
        {
            if (source is ScrollViewer nested)
            {
                if (nested.TemplatedParent is TextBox textBox)
                {
                    if (textBox.AcceptsReturn &&
                        textBox.VerticalScrollBarVisibility != ScrollBarVisibility.Disabled &&
                        textBox.ExtentHeight > textBox.ViewportHeight + 0.5)
                        return true;
                    source = textBox;
                    continue;
                }
                if (nested.TemplatedParent is ComboBox comboBox)
                {
                    if (comboBox.IsDropDownOpen) return true;
                    source = comboBox;
                    continue;
                }
                return true;
            }
            source = GetParent(source);
        }
        return false;
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject root)
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is ScrollViewer viewer) return viewer;
            if (FindScrollViewer(child) is { } nested) return nested;
        }
        return null;
    }

    private static DependencyObject? GetParent(DependencyObject child) =>
        child is Visual
            ? VisualTreeHelper.GetParent(child)
            : LogicalTreeHelper.GetParent(child);

    private sealed class ScrollState
    {
        public ScrollState()
        {
        }

        public double TargetOffset { get; set; }
        public double LastRenderingSeconds { get; set; } = -1;
        public double Velocity { get; set; }
        public bool IsAnimating { get; set; }
        public EventHandler? RenderHandler { get; set; }
    }
}

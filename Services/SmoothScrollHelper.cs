using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace DeskFolders.Services;

public static class SmoothScrollHelper
{
    private sealed class ScrollState
    {
        public ScrollViewer Viewer { get; }
        public double CurrentOffset { get; set; }
        public double TargetOffset { get; set; }
        public long LastTimestamp { get; set; }
        public bool IsAnimating { get; set; }

        public ScrollState(ScrollViewer viewer) => Viewer = viewer;
    }

    private static readonly Dictionary<ScrollViewer, ScrollState> States = new();

    public static void Attach(ScrollViewer viewer)
    {
        if (States.ContainsKey(viewer))
            return;

        var state = new ScrollState(viewer);
        States[viewer] = state;

        viewer.PreviewMouseWheel += (_, e) =>
        {
            if (viewer.ComputedVerticalScrollBarVisibility != System.Windows.Visibility.Visible ||
                viewer.ScrollableHeight <= 0)
                return;

            e.Handled = true;

            if (!state.IsAnimating)
            {
                state.CurrentOffset = viewer.VerticalOffset;
                state.TargetOffset = viewer.VerticalOffset;
                state.LastTimestamp = Stopwatch.GetTimestamp();
            }

            state.TargetOffset = Math.Clamp(state.TargetOffset - e.Delta * 0.65, 0, viewer.ScrollableHeight);

            if (!state.IsAnimating)
            {
                state.IsAnimating = true;
                CompositionTarget.Rendering += OnRender;
            }
        };

        viewer.Unloaded += (_, _) =>
        {
            if (state.IsAnimating)
            {
                state.IsAnimating = false;
                CompositionTarget.Rendering -= OnRender;
            }
            States.Remove(viewer);
        };

        void OnRender(object? s, EventArgs e)
        {
            if (!state.IsAnimating)
                return;

            long now = Stopwatch.GetTimestamp();
            double dt = Math.Clamp((now - state.LastTimestamp) / (double)Stopwatch.Frequency, 0.001, 0.05);
            state.LastTimestamp = now;

            double diff = state.TargetOffset - state.CurrentOffset;
            if (Math.Abs(diff) < 0.35)
            {
                state.CurrentOffset = state.TargetOffset;
                viewer.ScrollToVerticalOffset(state.TargetOffset);
                state.IsAnimating = false;
                CompositionTarget.Rendering -= OnRender;
                return;
            }

            // Экспоненциальное затухание: одинаковая плавность на 60Hz, 144Hz и 240Hz
            double alpha = 1.0 - Math.Exp(-18.0 * dt);
            state.CurrentOffset += diff * alpha;
            viewer.ScrollToVerticalOffset(state.CurrentOffset);
        }
    }
}
using System.Windows;

namespace SessionDock;

internal enum GuidedTourCalloutSide
{
    Below,
    Above,
    Right,
    Left,
    Fallback
}

internal readonly record struct GuidedTourCalloutPlacement(
    Rect Bounds,
    GuidedTourCalloutSide Side);

internal static class GuidedTourPlacementPolicy
{
    private const double GeometryTolerance = 0.01;

    internal static GuidedTourCalloutPlacement Calculate(
        Size viewport,
        Rect highlight,
        Size desiredCallout,
        double edgeMargin,
        double preferredGap,
        double minimumGap,
        double minimumReadableWidth,
        double minimumReadableHeight)
    {
        if (!IsFinitePositive(viewport.Width) ||
            !IsFinitePositive(viewport.Height))
        {
            throw new ArgumentOutOfRangeException(nameof(viewport));
        }
        if (highlight.IsEmpty ||
            !IsFinitePositive(highlight.Width) ||
            !IsFinitePositive(highlight.Height))
        {
            throw new ArgumentOutOfRangeException(nameof(highlight));
        }
        if (!IsFinitePositive(desiredCallout.Width) ||
            !IsFinitePositive(desiredCallout.Height))
        {
            throw new ArgumentOutOfRangeException(nameof(desiredCallout));
        }
        if (!double.IsFinite(edgeMargin) || edgeMargin < 0 ||
            !double.IsFinite(preferredGap) || preferredGap < 0 ||
            !double.IsFinite(minimumGap) || minimumGap < 0 ||
            minimumGap > preferredGap ||
            !IsFinitePositive(minimumReadableWidth) ||
            !IsFinitePositive(minimumReadableHeight))
        {
            throw new ArgumentOutOfRangeException(nameof(edgeMargin));
        }

        var horizontalMargin = Math.Min(
            edgeMargin,
            Math.Max(0, (viewport.Width - 1) / 2));
        var verticalMargin = Math.Min(
            edgeMargin,
            Math.Max(0, (viewport.Height - 1) / 2));
        var safeBounds = new Rect(
            horizontalMargin,
            verticalMargin,
            Math.Max(1, viewport.Width - (horizontalMargin * 2)),
            Math.Max(1, viewport.Height - (verticalMargin * 2)));
        var boundedHighlight = Rect.Intersect(
            highlight,
            new Rect(0, 0, viewport.Width, viewport.Height));
        if (boundedHighlight.IsEmpty)
            boundedHighlight = highlight;

        var candidates = CreateCandidates(
            safeBounds,
            boundedHighlight,
            desiredCallout,
            minimumGap,
            minimumReadableWidth,
            minimumReadableHeight);
        var selected = candidates
            .Where(candidate => candidate.FullyFits)
            .OrderBy(candidate => candidate.Priority)
            .FirstOrDefault();
        if (selected is null)
        {
            selected = candidates
                .Where(candidate => candidate.IsReadable)
                .OrderByDescending(candidate => candidate.Retention)
                .ThenBy(candidate => candidate.Priority)
                .FirstOrDefault();
        }
        selected ??= candidates
            .OrderByDescending(candidate => candidate.Retention)
            .ThenBy(candidate => candidate.Priority)
            .FirstOrDefault();

        if (selected is not null)
        {
            return new GuidedTourCalloutPlacement(
                AlignCandidate(
                    selected,
                    boundedHighlight,
                    preferredGap,
                    minimumGap),
                selected.Side);
        }

        return new GuidedTourCalloutPlacement(
            FindLeastOverlappingFallback(
                safeBounds,
                boundedHighlight,
                desiredCallout),
            GuidedTourCalloutSide.Fallback);
    }

    private static IReadOnlyList<PlacementCandidate> CreateCandidates(
        Rect safeBounds,
        Rect highlight,
        Size desiredCallout,
        double minimumGap,
        double minimumReadableWidth,
        double minimumReadableHeight)
    {
        var belowTop = Math.Clamp(
            highlight.Bottom + minimumGap,
            safeBounds.Top,
            safeBounds.Bottom);
        var aboveBottom = Math.Clamp(
            highlight.Top - minimumGap,
            safeBounds.Top,
            safeBounds.Bottom);
        var rightLeft = Math.Clamp(
            highlight.Right + minimumGap,
            safeBounds.Left,
            safeBounds.Right);
        var leftRight = Math.Clamp(
            highlight.Left - minimumGap,
            safeBounds.Left,
            safeBounds.Right);
        var slots = new[]
        {
            new PlacementSlot(
                GuidedTourCalloutSide.Below,
                0,
                new Rect(
                    safeBounds.Left,
                    belowTop,
                    safeBounds.Width,
                    Math.Max(0, safeBounds.Bottom - belowTop))),
            new PlacementSlot(
                GuidedTourCalloutSide.Above,
                1,
                new Rect(
                    safeBounds.Left,
                    safeBounds.Top,
                    safeBounds.Width,
                    Math.Max(0, aboveBottom - safeBounds.Top))),
            new PlacementSlot(
                GuidedTourCalloutSide.Right,
                2,
                new Rect(
                    rightLeft,
                    safeBounds.Top,
                    Math.Max(0, safeBounds.Right - rightLeft),
                    safeBounds.Height)),
            new PlacementSlot(
                GuidedTourCalloutSide.Left,
                3,
                new Rect(
                    safeBounds.Left,
                    safeBounds.Top,
                    Math.Max(0, leftRight - safeBounds.Left),
                    safeBounds.Height))
        };

        var requiredWidth = Math.Min(
            desiredCallout.Width,
            minimumReadableWidth);
        var requiredHeight = Math.Min(
            desiredCallout.Height,
            minimumReadableHeight);
        var candidates = new List<PlacementCandidate>(slots.Length);
        foreach (var slot in slots)
        {
            if (slot.Bounds.Width <= GeometryTolerance ||
                slot.Bounds.Height <= GeometryTolerance)
            {
                continue;
            }

            var width = Math.Min(desiredCallout.Width, slot.Bounds.Width);
            var height = Math.Min(desiredCallout.Height, slot.Bounds.Height);
            candidates.Add(new PlacementCandidate(
                slot.Side,
                slot.Priority,
                slot.Bounds,
                new Size(width, height),
                width + GeometryTolerance >= desiredCallout.Width &&
                height + GeometryTolerance >= desiredCallout.Height,
                width + GeometryTolerance >= requiredWidth &&
                height + GeometryTolerance >= requiredHeight,
                (width / desiredCallout.Width) *
                (height / desiredCallout.Height)));
        }
        return candidates;
    }

    private static Rect AlignCandidate(
        PlacementCandidate candidate,
        Rect highlight,
        double preferredGap,
        double minimumGap)
    {
        var slot = candidate.Slot;
        var size = candidate.Size;
        var additionalGap = candidate.Side switch
        {
            GuidedTourCalloutSide.Below or
            GuidedTourCalloutSide.Above => Math.Min(
                preferredGap - minimumGap,
                Math.Max(0, slot.Height - size.Height)),
            GuidedTourCalloutSide.Right or
            GuidedTourCalloutSide.Left => Math.Min(
                preferredGap - minimumGap,
                Math.Max(0, slot.Width - size.Width)),
            _ => 0
        };

        var left = candidate.Side switch
        {
            GuidedTourCalloutSide.Right => slot.Left + additionalGap,
            GuidedTourCalloutSide.Left =>
                slot.Right - size.Width - additionalGap,
            _ => ClampToRange(
                highlight.Left,
                slot.Left,
                slot.Right - size.Width)
        };
        var top = candidate.Side switch
        {
            GuidedTourCalloutSide.Below => slot.Top + additionalGap,
            GuidedTourCalloutSide.Above =>
                slot.Bottom - size.Height - additionalGap,
            _ => ClampToRange(
                highlight.Top,
                slot.Top,
                slot.Bottom - size.Height)
        };
        return new Rect(left, top, size.Width, size.Height);
    }

    private static Rect FindLeastOverlappingFallback(
        Rect safeBounds,
        Rect highlight,
        Size desiredCallout)
    {
        var width = Math.Min(desiredCallout.Width, safeBounds.Width);
        var height = Math.Min(desiredCallout.Height, safeBounds.Height);
        var right = safeBounds.Right - width;
        var bottom = safeBounds.Bottom - height;
        var candidates = new[]
        {
            new Rect(safeBounds.Left, safeBounds.Top, width, height),
            new Rect(right, safeBounds.Top, width, height),
            new Rect(safeBounds.Left, bottom, width, height),
            new Rect(right, bottom, width, height)
        };
        return candidates
            .OrderBy(candidate => IntersectionArea(candidate, highlight))
            .ThenBy(candidate => candidate.Top)
            .ThenBy(candidate => candidate.Left)
            .First();
    }

    private static double IntersectionArea(Rect first, Rect second)
    {
        var intersection = Rect.Intersect(first, second);
        return intersection.IsEmpty
            ? 0
            : intersection.Width * intersection.Height;
    }

    private static double ClampToRange(
        double value,
        double minimum,
        double maximum) =>
        maximum <= minimum
            ? minimum
            : Math.Clamp(value, minimum, maximum);

    private static bool IsFinitePositive(double value) =>
        double.IsFinite(value) && value > 0;

    private sealed record PlacementSlot(
        GuidedTourCalloutSide Side,
        int Priority,
        Rect Bounds);

    private sealed record PlacementCandidate(
        GuidedTourCalloutSide Side,
        int Priority,
        Rect Slot,
        Size Size,
        bool FullyFits,
        bool IsReadable,
        double Retention);
}

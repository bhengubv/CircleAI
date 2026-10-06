// WindowsAutomationHelpers.cs
//
// (3.3.0) Top-up: helpers for building UiElement records (hit-test,
// containment, formatted dump for debugging).

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace CircleAI.WindowsAutomation;

public static class UiElementHelpers
{
    public static bool ContainsPoint(this UiElement el, int x, int y)
    {
        ArgumentNullException.ThrowIfNull(el);
        return x >= el.X && y >= el.Y && x < el.X + el.Width && y < el.Y + el.Height;
    }

    public static IReadOnlyList<UiElement> HitTest(IEnumerable<UiElement> elements, int x, int y)
    {
        ArgumentNullException.ThrowIfNull(elements);
        return elements.Where(e => e.ContainsPoint(x, y)).ToArray();
    }

    public static string Dump(IEnumerable<UiElement> elements)
    {
        ArgumentNullException.ThrowIfNull(elements);
        var sb = new StringBuilder();
        foreach (var e in elements)
        {
            sb.Append(e.ElementId).Append(" \"").Append(e.Name).Append("\" ")
              .Append(e.Kind).Append(" @ (").Append(e.X).Append(",").Append(e.Y)
              .Append(") ").Append(e.Width).Append('x').Append(e.Height).Append('\n');
        }
        return sb.ToString();
    }
}

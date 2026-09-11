using System;
using System.Windows;
using System.Windows.Controls;

namespace StageManagerApp
{
    public class CascadingPanel : Panel
    {
        public double OffsetX { get; set; } = 15;
        public double OffsetY { get; set; } = 15;

        protected override Size MeasureOverride(Size availableSize)
        {
            Size resultSize = new Size(0, 0);
            var children = InternalChildren;
            for (int i = 0; i < children.Count; i++)
            {
                UIElement child = children[i];
                child.Measure(availableSize);
                resultSize.Width = Math.Max(resultSize.Width, child.DesiredSize.Width);
                resultSize.Height = Math.Max(resultSize.Height, child.DesiredSize.Height);
            }
            if (InternalChildren.Count > 1)
            {
                resultSize.Width += OffsetX * (InternalChildren.Count - 1);
                resultSize.Height += OffsetY * (InternalChildren.Count - 1);
            }
            return resultSize;
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            double x = 0;
            double y = 0;
            var children = InternalChildren;
            for (int i = 0; i < children.Count; i++)
            {
                UIElement child = children[i];
                child.Arrange(new Rect(x, y, child.DesiredSize.Width, child.DesiredSize.Height));
                x += OffsetX;
                y += OffsetY;
            }
            return finalSize;
        }
    }
}

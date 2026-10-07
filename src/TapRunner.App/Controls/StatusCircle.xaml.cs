using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using TapRunner.App.Models;

namespace TapRunner.App.Controls
{
    /// <summary>
    /// A labeled circle that can be Off (dim gray), Partial (its color at reduced opacity - a
    /// fail/error a parent step cleared), Full (its color, flashing if configured) or Flashing
    /// (its color, always flashing - the live verdict during a run).
    /// </summary>
    public partial class StatusCircle : UserControl
    {
        static readonly Brush OffBrush = new SolidColorBrush(Color.FromRgb(0x80, 0x80, 0x80));

        public StatusCircle()
        {
            InitializeComponent();
        }

        public static readonly DependencyProperty LabelProperty = DependencyProperty.Register(
            nameof(Label), typeof(string), typeof(StatusCircle), new PropertyMetadata("", OnLabelChanged));

        public string Label
        {
            get => (string)GetValue(LabelProperty);
            set => SetValue(LabelProperty, value);
        }

        static void OnLabelChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
            => ((StatusCircle)d).LabelText.Text = (string)e.NewValue;

        public static readonly DependencyProperty OnColorProperty = DependencyProperty.Register(
            nameof(OnColor), typeof(Color), typeof(StatusCircle), new PropertyMetadata(Colors.Gray, OnAppearanceChanged));

        public Color OnColor
        {
            get => (Color)GetValue(OnColorProperty);
            set => SetValue(OnColorProperty, value);
        }

        public static readonly DependencyProperty FlashProperty = DependencyProperty.Register(
            nameof(Flash), typeof(bool), typeof(StatusCircle), new PropertyMetadata(false, OnAppearanceChanged));

        public bool Flash
        {
            get => (bool)GetValue(FlashProperty);
            set => SetValue(FlashProperty, value);
        }

        public static readonly DependencyProperty StateProperty = DependencyProperty.Register(
            nameof(State), typeof(CircleState), typeof(StatusCircle), new PropertyMetadata(CircleState.Off, OnAppearanceChanged));

        public CircleState State
        {
            get => (CircleState)GetValue(StateProperty);
            set => SetValue(StateProperty, value);
        }

        static void OnAppearanceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
            => ((StatusCircle)d).UpdateAppearance();

        void UpdateAppearance()
        {
            var storyboard = (Storyboard)Resources["FlashStoryboard"];
            storyboard.Stop(this);

            switch (State)
            {
                case CircleState.Off:
                    Circle.Fill = OffBrush;
                    Circle.Opacity = 1.0;
                    break;

                case CircleState.Partial:
                    Circle.Fill = new SolidColorBrush(OnColor);
                    Circle.Opacity = 0.4;
                    break;

                case CircleState.Full:
                    Circle.Fill = new SolidColorBrush(OnColor);
                    Circle.Opacity = 1.0;
                    if (Flash)
                        storyboard.Begin(this, true);
                    break;

                case CircleState.Flashing:
                    Circle.Fill = new SolidColorBrush(OnColor);
                    Circle.Opacity = 1.0;
                    storyboard.Begin(this, true);
                    break;
            }
        }
    }
}

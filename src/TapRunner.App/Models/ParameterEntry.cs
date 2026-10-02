using System;
using System.ComponentModel;
using System.Windows;

namespace TapRunner.App.Models
{
    /// <summary>One external test plan parameter, bound to a row in the parameters list.</summary>
    public class ParameterEntry : INotifyPropertyChanged
    {
        // Every external parameter in these plans lives under one "Parameters" group, so this
        // prefix is on every single row - it tells the operator nothing and just takes up space.
        // Any other/deeper group name (e.g. "Input \ Value") is left as-is, since that does
        // distinguish it from other parameters.
        const string RedundantGroupPrefix = "Parameters \\ ";

        /// <summary>The exact external parameter name OpenTAP knows this by - used for "-e Name=Value" when running.</summary>
        public string FullName { get; }

        /// <summary>Display name shown in the UI: <see cref="FullName"/> with the redundant leading "Parameters \ " stripped, if present.</summary>
        public string Name { get; }

        /// <summary>The default value as read from the TapPlan file itself.</summary>
        public string PlanDefaultValue { get; }

        /// <summary>True if this looks like a serial number field (name contains "serial").</summary>
        public bool IsSerialNumber { get; }

        string value = "";
        public string Value
        {
            get => value;
            set { this.value = value; Raise(nameof(Value)); }
        }

        /// <summary>True if Settings marks this parameter Hidden. Display-only - it is still passed to tap.exe.</summary>
        public bool IsHidden { get; private set; }

        Visibility rowVisibility = Visibility.Visible;
        /// <summary>Collapsed for a hidden parameter, unless Settings says to show hidden ones greyed out.</summary>
        public Visibility RowVisibility
        {
            get => rowVisibility;
            private set { rowVisibility = value; Raise(nameof(RowVisibility)); }
        }

        double rowOpacity = 1.0;
        /// <summary>Dimmed for a hidden parameter shown greyed out (still editable); 1 otherwise.</summary>
        public double RowOpacity
        {
            get => rowOpacity;
            private set { rowOpacity = value; Raise(nameof(RowOpacity)); }
        }

        public ParameterEntry(string fullName, string planDefaultValue)
        {
            FullName = fullName;
            Name = fullName.StartsWith(RedundantGroupPrefix, StringComparison.Ordinal)
                ? fullName.Substring(RedundantGroupPrefix.Length)
                : fullName;
            PlanDefaultValue = planDefaultValue;
            IsSerialNumber = Name.IndexOf("serial", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>
        /// Applies the Settings "Hidden" choice for this parameter: hidden ones disappear from the
        /// list, or - with <paramref name="greyOut"/> - stay visible but dimmed and still editable.
        /// </summary>
        public void ApplyHiddenSetting(bool isHidden, bool greyOut)
        {
            IsHidden = isHidden;
            Raise(nameof(IsHidden));
            RowVisibility = isHidden && !greyOut ? Visibility.Collapsed : Visibility.Visible;
            RowOpacity = isHidden && greyOut ? 0.45 : 1.0;
        }

        void Raise(string propertyName) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

        public event PropertyChangedEventHandler PropertyChanged;
    }
}

using System;
using System.ComponentModel;

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
            set { this.value = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Value))); }
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

        public event PropertyChangedEventHandler PropertyChanged;
    }
}

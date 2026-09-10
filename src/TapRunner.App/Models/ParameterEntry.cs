using System;
using System.ComponentModel;

namespace TapRunner.App.Models
{
    /// <summary>One external test plan parameter, bound to a row in the parameters list.</summary>
    public class ParameterEntry : INotifyPropertyChanged
    {
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

        public ParameterEntry(string name, string planDefaultValue)
        {
            Name = name;
            PlanDefaultValue = planDefaultValue;
            IsSerialNumber = name.IndexOf("serial", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        public event PropertyChangedEventHandler PropertyChanged;
    }
}

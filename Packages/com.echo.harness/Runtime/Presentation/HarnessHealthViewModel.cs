using System;
using Unity.Properties;

namespace Echo.Harness.Presentation
{
    public sealed class HarnessHealthViewModel
    {
        public HarnessHealthViewModel(string status)
        {
            if (string.IsNullOrWhiteSpace(status))
            {
                throw new ArgumentException("A Harness status is required.", nameof(status));
            }

            Status = status;
        }

        [CreateProperty]
        public string Status { get; }
    }
}

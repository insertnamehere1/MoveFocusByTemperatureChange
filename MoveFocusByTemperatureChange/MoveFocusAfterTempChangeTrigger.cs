using Namotion.Reflection;
using Newtonsoft.Json;
using NINA.Core.Model;
using NINA.Core.Utility.Notification;
using NINA.Equipment.Equipment.MyGuider;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Sequencer.Container;
using NINA.Sequencer.SequenceItem;
using NINA.Sequencer.Trigger;
using NINA.Sequencer.Validations;
using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace ChrisDowd.NINA.MoveFocusAfterTempChange.MoveFocusAfterTempChangeTestCategory {
    [ExportMetadata("Name", "Move Focus after ΔTemperature")]
    [ExportMetadata("Description", "This trigger will move focus based on temperature change when a set temperature change occurs")]
    [ExportMetadata("Icon", "Plugin_Test_SVG")]
    [ExportMetadata("Category", "Utility")]
    [Export(typeof(ISequenceTrigger))]
    [JsonObject(MemberSerialization.OptIn)]
    public class MoveFocusAfterTempChangeTrigger : SequenceTrigger, IValidatable {
        private const string UpTo10DecimalsFormat = "0.0#########";

        private readonly IFocuserMediator focuser;
        private readonly IGuiderMediator guider;

        // Settings
        private float temperatureDelta = 0.1f;
        private bool absolute = false; // default: Relative (off/left)
        private double slope = 0.0;
        private double intercept = 0.0;

        // Runtime state (not persisted, as requested)
        private double lastTemperature = double.NaN;
        private double absoluteRemainder = 0.0;
        private double relativeRemainder = 0.0;

        // Debug/diagnostics
        private int updateCount = 0;

        // Validation
        private readonly List<string> issues = new List<string>();
        private List<string> lastIssuesSnapshot = new List<string>();

        [ImportingConstructor]
        public MoveFocusAfterTempChangeTrigger(IFocuserMediator focuser, IGuiderMediator guider) {
            this.focuser = focuser;
            this.guider = guider;
        }

        public override object Clone() {
            return new MoveFocusAfterTempChangeTrigger(focuser, guider) {
                Icon = Icon,
                Name = Name,
                Category = Category,
                Description = Description,

                Absolute = this.Absolute,
                TemperatureDelta = this.TemperatureDelta,
                Slope = this.Slope,
                Intercept = this.Intercept
            };
        }

        public override bool ShouldTrigger(ISequenceItem previousItem, ISequenceItem nextItem) {
            var focuserInfo = focuser?.GetInfo();
            if (focuserInfo == null || !focuserInfo.Connected)
                return false;

            var guiderInfo = guider?.GetInfo();
            if (guiderInfo == null || !guiderInfo.Connected)
                return false;

            double currentTemp = focuserInfo.Temperature;
            if (double.IsNaN(currentTemp) || double.IsInfinity(currentTemp))
                return false;

            // First run: initialise baseline and do not trigger.
            if (double.IsNaN(lastTemperature)) {
                lastTemperature = currentTemp;
                return false;
            }

            double deltaThreshold = TemperatureDelta;
            if (double.IsNaN(deltaThreshold) || deltaThreshold <= 0)
                return false;

            double change = Math.Abs(currentTemp - lastTemperature);
            if (change >= deltaThreshold) 
                return true;

            return false;
        }

        public override async Task Execute(
            ISequenceContainer context,
            IProgress<ApplicationStatus> progress,
            CancellationToken token) {
            var focuserInfo = focuser?.GetInfo();

            if (focuserInfo == null || !focuserInfo.Connected) {
                Notification.ShowError("Focuser is not connected.");
                return;
            }

            var guiderInfo = guider?.GetInfo();

            if (guiderInfo == null || !guiderInfo.Connected) {
                Notification.ShowError("Guider is not connected.");
                return;
            }

            double currentTemp = focuserInfo.Temperature;
            if (double.IsNaN(currentTemp) || double.IsInfinity(currentTemp)) {
                Notification.ShowError("Focuser temperature is not available.");
                return;
            }

            // Extra safety: if we ever arrive here with no baseline, initialise and exit
            if (double.IsNaN(lastTemperature)) {
                lastTemperature = currentTemp;
                return;
            }

            bool guidingStopped = false;

            try {
                if (Absolute) {
                    // Absolute target position: position = m*T + b
                    double exactPosition = (currentTemp * Slope) + Intercept;

                    // Carry forward fractional remainder
                    exactPosition += absoluteRemainder;

                    if (exactPosition > int.MaxValue || exactPosition < int.MinValue) {
                        Notification.ShowError("Calculated focuser position is out of range.");
                        return;
                    }

                    int intPosition = (int)Math.Round(exactPosition, MidpointRounding.AwayFromZero);
                    absoluteRemainder = exactPosition - intPosition;

                    // If we can read current position, avoid needless guiding interruption and moves
                    int? currentPosition = null;
                    try {
                        currentPosition = focuserInfo.Position;
                    } catch {
                        // Some focusers/info objects may not expose Position reliably.
                        // If not available, we still proceed with the move.
                    }

                    if (currentPosition.HasValue && currentPosition.Value == intPosition) {
                        // No move needed
                        return;
                    }

                    await guider.StopGuiding(token);
                    guidingStopped = true;

                    await focuser.MoveFocuser(intPosition, token);

                    // For absolute mode, lastTemperature baseline can move forward too
                    lastTemperature = currentTemp;
                } else {
                    double tempDelta = currentTemp - lastTemperature;

                    // Exact relative move in double
                    double exactSteps = (tempDelta * Slope) + relativeRemainder;

                    if (exactSteps > int.MaxValue || exactSteps < int.MinValue) {
                        Notification.ShowError("Calculated focuser steps are out of range.");
                        return;
                    }

                    int intSteps = (int)Math.Round(exactSteps, MidpointRounding.AwayFromZero);
                    relativeRemainder = exactSteps - intSteps;

                    if (intSteps == 0) {
                        // Update baseline so we do not keep triggering on the same delta
                        lastTemperature = currentTemp;
                        return;
                    }

                    await guider.StopGuiding(token);
                    guidingStopped = true;

                    await focuser.MoveFocuserRelative(intSteps, token);

                    // Update baseline after applying the move
                    lastTemperature = currentTemp;
                }

                Notification.ShowSuccess("Temperature compensation applied.");
            } finally {
                if (guidingStopped)
                    await guider.StartGuiding(false, null, token);

                UpdateCount = UpdateCount + 1;
            }
        }

        public override string ToString() {
            return $"Category: {Category}, Item: {nameof(MoveFocusAfterTempChangeTrigger)}";
        }

        bool IValidatable.Validate() {
            issues.Clear();

            if (focuser == null || !(focuser.GetInfo()?.Connected ?? false))
                issues.Add("Focuser is not connected.");

            if (guider == null || !(guider.GetInfo()?.Connected ?? false))
                issues.Add("Guider is not connected.");

            if (Slope == 0)
                issues.Add("Slope is zero. Temperature compensation will have no effect.");

            bool changed = !issues.SequenceEqual(lastIssuesSnapshot);
            if (changed) {
                lastIssuesSnapshot = new List<string>(issues);
                RaisePropertyChanged(nameof(Issues));
            }

            return issues.Count == 0;
        }

        public IList<string> Issues => issues;

        [JsonProperty]
        public float TemperatureDelta {
            get => temperatureDelta;
            set {
                if (temperatureDelta != value) {
                    temperatureDelta = (float)Math.Round(value, 1);
                    RaisePropertyChanged(nameof(TemperatureDelta));
                    RaisePropertyChanged(nameof(TemperatureDeltaText));
                }
            }
        }

        public string TemperatureDeltaText {
            get => temperatureDelta.ToString("F1", CultureInfo.InvariantCulture);
            set {
                if (string.IsNullOrWhiteSpace(value)) {
                    RaisePropertyChanged(nameof(TemperatureDeltaText));
                    return;
                }

                if (float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out float parsed)) {
                    TemperatureDelta = parsed;
                }
            }
        }

        [JsonProperty]
        public bool Absolute {
            get => absolute;
            set {
                if (absolute != value) {
                    absolute = value;
                    RaisePropertyChanged(nameof(Absolute));
                    RaisePropertyChanged(nameof(IsInterceptEnabled));
                }
            }
        }

        public bool IsInterceptEnabled => Absolute;

        [JsonProperty]
        public double Slope {
            get => slope;
            set {
                if (slope != value) {
                    bool affectsValidation = (slope == 0) || (value == 0);

                    slope = value;
                    RaisePropertyChanged(nameof(Slope));
                    RaisePropertyChanged(nameof(SlopeText));

                    // Helps the warning icon clear/appear without waiting for a separate validation cycle
                    if (affectsValidation)
                        RaisePropertyChanged(nameof(Issues));
                }
            }
        }

        public string SlopeText {
            get => slope.ToString(UpTo10DecimalsFormat, CultureInfo.InvariantCulture);
            set {
                if (string.IsNullOrWhiteSpace(value)) {
                    RaisePropertyChanged(nameof(SlopeText));
                    return;
                }

                if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)) {
                    Slope = parsed;
                }
            }
        }

        [JsonProperty]
        public double Intercept {
            get => intercept;
            set {
                if (intercept != value) {
                    intercept = value;
                    RaisePropertyChanged(nameof(Intercept));
                    RaisePropertyChanged(nameof(InterceptText));
                }
            }
        }

        public string InterceptText {
            get => intercept.ToString(UpTo10DecimalsFormat, CultureInfo.InvariantCulture);
            set {
                if (string.IsNullOrWhiteSpace(value)) {
                    RaisePropertyChanged(nameof(InterceptText));
                    return;
                }

                if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)) {
                    Intercept = parsed;
                }
            }
        }

        public int UpdateCount {
            get => updateCount;
            private set {
                if (updateCount != value) {
                    updateCount = value;
                    RaisePropertyChanged(nameof(UpdateCount));
                }
            }
        }
    }
}
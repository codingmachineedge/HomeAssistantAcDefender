using HomeAssistantAcDefender.Models;

namespace HomeAssistantAcDefender.Services;

/// <summary>
/// A small, dependency-free online machine-learning trainer. It fits two models by batch gradient
/// descent:
///   • a logistic-regression classifier that predicts how likely the current time/context is to make
///     people upset, learned from the someone-upset button (positives) versus ordinary operating
///     contexts (negatives); and
///   • a linear-regression model of the preferred wall setpoint by time of day, learned from the real
///     Home Assistant history plus live human wall choices.
/// It is pure and deterministic (no <see cref="Random"/>, no clock), so it is fully unit-testable and
/// safe to run inside the 24/7 worker. Its outputs feed the existing safety-bounded apply paths
/// (anger → extra hands-off grace; comfort → Comfort Memory), never the safety bands themselves.
/// </summary>
public sealed class LearningTrainer
{
    public const int AngerFeatureCount = 5;
    public const int ComfortFeatureCount = 2;
    public const int InterferenceFeatureCount = 6;
    public const int OverrideCadenceFeatureCount = 2;

    private const int Epochs = 500;
    private const double LearningRate = 0.2;
    private const double L2 = 0.0015;

    public static double[] AngerFeatures(int hourOfDay, double defenderPushCelsius, int recentTouchCount, double roomAboveTargetCelsius)
    {
        var angle = 2.0 * Math.PI * (((hourOfDay % 24) + 24) % 24) / 24.0;
        return
        [
            Math.Sin(angle),
            Math.Cos(angle),
            Math.Clamp(defenderPushCelsius / 3.0, -2.0, 2.0),
            Math.Clamp(recentTouchCount / 6.0, 0.0, 3.0),
            Math.Clamp(roomAboveTargetCelsius / 3.0, -2.0, 2.0),
        ];
    }

    public static double[] ComfortFeatures(int hourOfDay)
    {
        var angle = 2.0 * Math.PI * (((hourOfDay % 24) + 24) % 24) / 24.0;
        return [Math.Sin(angle), Math.Cos(angle)];
    }

    /// <summary>Features for the interference classifier: time of day (cyclic), who is around, whether power
    /// is expensive, how hot the room is vs target, and recent override pressure.</summary>
    public static double[] InterferenceFeatures(int hourOfDay, bool ownerHome, bool bedroomOccupied, bool peakPower, double roomAboveTargetCelsius, int recentOverrideCount)
    {
        var angle = 2.0 * Math.PI * (((hourOfDay % 24) + 24) % 24) / 24.0;
        return
        [
            Math.Sin(angle),
            Math.Cos(angle),
            ownerHome ? 1.0 : 0.0,
            bedroomOccupied ? 1.0 : 0.0,
            peakPower ? 1.0 : 0.0,
            Math.Clamp(recentOverrideCount / 4.0, 0.0, 4.0),
        ];
    }

    /// <summary>Features for the override-cadence regressor: time of day (cyclic).</summary>
    public static double[] OverrideCadenceFeatures(int hourOfDay)
    {
        var angle = 2.0 * Math.PI * (((hourOfDay % 24) + 24) % 24) / 24.0;
        return [Math.Sin(angle), Math.Cos(angle)];
    }

    /// <summary>Fits both models from the supplied real samples, warm-starting from any prior weights.</summary>
    public LearningModelState Train(
        IReadOnlyList<(double[] Features, int Label)> angerSamples,
        IReadOnlyList<(double[] Features, double Target)> comfortSamples,
        LearningModelState? warmStart,
        IReadOnlyList<(double[] Features, int Label)>? interferenceSamples = null,
        IReadOnlyList<(double[] Features, double Target)>? overrideCadenceSamples = null)
    {
        var model = new LearningModelState
        {
            AngerWeights = ResizeOrZero(warmStart?.AngerWeights, AngerFeatureCount),
            AngerBias = warmStart?.AngerBias ?? 0.0,
            ComfortWeights = ResizeOrZero(warmStart?.ComfortWeights, ComfortFeatureCount),
            ComfortBias = warmStart?.ComfortBias ?? 0.0,
            AngerPositiveSamples = warmStart?.AngerPositiveSamples ?? 0,
            AngerNegativeSamples = warmStart?.AngerNegativeSamples ?? 0,
            ComfortSamples = warmStart?.ComfortSamples ?? 0,
            AngerLogLoss = warmStart?.AngerLogLoss ?? 0,
            ComfortRmse = warmStart?.ComfortRmse ?? 0,
            InterferenceWeights = ResizeOrZero(warmStart?.InterferenceWeights, InterferenceFeatureCount),
            InterferenceBias = warmStart?.InterferenceBias ?? 0.0,
            InterferencePositiveSamples = warmStart?.InterferencePositiveSamples ?? 0,
            InterferenceNegativeSamples = warmStart?.InterferenceNegativeSamples ?? 0,
            InterferenceLogLoss = warmStart?.InterferenceLogLoss ?? 0,
            OverrideCadenceWeights = ResizeOrZero(warmStart?.OverrideCadenceWeights, OverrideCadenceFeatureCount),
            OverrideCadenceBias = warmStart?.OverrideCadenceBias ?? 0.0,
            OverrideCadenceSamples = warmStart?.OverrideCadenceSamples ?? 0,
            OverrideCadenceRmse = warmStart?.OverrideCadenceRmse ?? 0,
        };

        var validAnger = angerSamples.Where(s => s.Features.Length == AngerFeatureCount).ToList();
        if (validAnger.Count > 0 && validAnger.Any(s => s.Label == 1) && validAnger.Any(s => s.Label == 0))
        {
            model.AngerBias = TrainLogistic(model.AngerWeights, model.AngerBias, validAnger);
            model.AngerPositiveSamples = validAnger.Count(s => s.Label == 1);
            model.AngerNegativeSamples = validAnger.Count(s => s.Label == 0);
            model.AngerLogLoss = Math.Round(LogLoss(model.AngerWeights, model.AngerBias, validAnger), 4);
        }

        var validComfort = comfortSamples.Where(s => s.Features.Length == ComfortFeatureCount).ToList();
        if (validComfort.Count > 0)
        {
            model.ComfortBias = TrainLinear(model.ComfortWeights, model.ComfortBias, validComfort);
            model.ComfortSamples = validComfort.Count;
            model.ComfortRmse = Math.Round(Rmse(model.ComfortWeights, model.ComfortBias, validComfort), 3);
        }

        var validInterference = (interferenceSamples ?? []).Where(s => s.Features.Length == InterferenceFeatureCount).ToList();
        if (validInterference.Count > 0 && validInterference.Any(s => s.Label == 1) && validInterference.Any(s => s.Label == 0))
        {
            model.InterferenceBias = TrainLogistic(model.InterferenceWeights, model.InterferenceBias, validInterference);
            model.InterferencePositiveSamples = validInterference.Count(s => s.Label == 1);
            model.InterferenceNegativeSamples = validInterference.Count(s => s.Label == 0);
            model.InterferenceLogLoss = Math.Round(LogLoss(model.InterferenceWeights, model.InterferenceBias, validInterference), 4);
        }

        var validCadence = (overrideCadenceSamples ?? []).Where(s => s.Features.Length == OverrideCadenceFeatureCount).ToList();
        if (validCadence.Count > 0)
        {
            model.OverrideCadenceBias = TrainLinear(model.OverrideCadenceWeights, model.OverrideCadenceBias, validCadence);
            model.OverrideCadenceSamples = validCadence.Count;
            model.OverrideCadenceRmse = Math.Round(Rmse(model.OverrideCadenceWeights, model.OverrideCadenceBias, validCadence), 3);
        }

        return model;
    }

    /// <summary>P(unwanted external override | context) in [0,1]. Returns 0 until both classes are seen.</summary>
    public double PredictInterferenceProbability(LearningModelState model, double[] features)
    {
        if (model.InterferencePositiveSamples <= 0 || model.InterferenceNegativeSamples <= 0 || model.InterferenceWeights.Length != features.Length)
        {
            return 0.0;
        }

        return Sigmoid(Dot(model.InterferenceWeights, features) + model.InterferenceBias);
    }

    /// <summary>Predicted minutes between consecutive overrides for the hour, or null until enough data.</summary>
    public double? PredictOverrideCadenceMinutes(LearningModelState model, double[] features)
    {
        if (model.OverrideCadenceSamples < 4 || model.OverrideCadenceWeights.Length != features.Length)
        {
            return null;
        }

        return Dot(model.OverrideCadenceWeights, features) + model.OverrideCadenceBias;
    }

    /// <summary>P(upset | context) in [0,1]. Returns 0 until the classifier has both classes to learn from.</summary>
    public double PredictAngerProbability(LearningModelState model, double[] features)
    {
        if (model.AngerPositiveSamples <= 0 || model.AngerNegativeSamples <= 0 || model.AngerWeights.Length != features.Length)
        {
            return 0.0;
        }

        return Sigmoid(Dot(model.AngerWeights, features) + model.AngerBias);
    }

    /// <summary>Predicted human-preferred setpoint, or null until enough comfort samples exist.</summary>
    public double? PredictPreferredSetPoint(LearningModelState model, double[] features)
    {
        if (model.ComfortSamples < 4 || model.ComfortWeights.Length != features.Length)
        {
            return null;
        }

        return Dot(model.ComfortWeights, features) + model.ComfortBias;
    }

    // Trains in place: mutates the supplied weights array and returns the fitted bias. Shared by every
    // logistic model (anger, interference) so one tested implementation backs them all.
    private static double TrainLogistic(double[] weights, double bias, IReadOnlyList<(double[] Features, int Label)> samples)
    {
        var w = weights;
        var b = bias;
        var n = samples.Count;
        for (var epoch = 0; epoch < Epochs; epoch++)
        {
            var gw = new double[w.Length];
            var gb = 0.0;
            foreach (var (x, y) in samples)
            {
                var error = Sigmoid(Dot(w, x) + b) - y;
                for (var j = 0; j < w.Length; j++)
                {
                    gw[j] += error * x[j];
                }

                gb += error;
            }

            for (var j = 0; j < w.Length; j++)
            {
                w[j] -= LearningRate * ((gw[j] / n) + (L2 * w[j]));
            }

            b -= LearningRate * (gb / n);
        }

        return b;
    }

    private static double TrainLinear(double[] weights, double bias, IReadOnlyList<(double[] Features, double Target)> samples)
    {
        var w = weights;
        var b = bias;
        var n = samples.Count;
        for (var epoch = 0; epoch < Epochs; epoch++)
        {
            var gw = new double[w.Length];
            var gb = 0.0;
            foreach (var (x, y) in samples)
            {
                var error = (Dot(w, x) + b) - y;
                for (var j = 0; j < w.Length; j++)
                {
                    gw[j] += error * x[j];
                }

                gb += error;
            }

            for (var j = 0; j < w.Length; j++)
            {
                w[j] -= LearningRate * ((gw[j] / n) + (L2 * w[j]));
            }

            b -= LearningRate * (gb / n);
        }

        return b;
    }

    private static double Sigmoid(double z) => 1.0 / (1.0 + Math.Exp(-Math.Clamp(z, -30.0, 30.0)));

    private static double Dot(double[] a, double[] b)
    {
        var sum = 0.0;
        for (var i = 0; i < a.Length && i < b.Length; i++)
        {
            sum += a[i] * b[i];
        }

        return sum;
    }

    private static double LogLoss(double[] weights, double bias, IReadOnlyList<(double[] Features, int Label)> samples)
    {
        var loss = 0.0;
        foreach (var (x, y) in samples)
        {
            var p = Math.Clamp(Sigmoid(Dot(weights, x) + bias), 1e-9, 1 - 1e-9);
            loss += -((y * Math.Log(p)) + ((1 - y) * Math.Log(1 - p)));
        }

        return loss / samples.Count;
    }

    private static double Rmse(double[] weights, double bias, IReadOnlyList<(double[] Features, double Target)> samples)
    {
        var sum = 0.0;
        foreach (var (x, y) in samples)
        {
            var error = (Dot(weights, x) + bias) - y;
            sum += error * error;
        }

        return Math.Sqrt(sum / samples.Count);
    }

    private static double[] ResizeOrZero(double[]? weights, int length)
    {
        if (weights is { } existing && existing.Length == length)
        {
            return (double[])existing.Clone();
        }

        return new double[length];
    }
}

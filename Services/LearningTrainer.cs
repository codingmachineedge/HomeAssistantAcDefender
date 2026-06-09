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

    /// <summary>Fits both models from the supplied real samples, warm-starting from any prior weights.</summary>
    public LearningModelState Train(
        IReadOnlyList<(double[] Features, int Label)> angerSamples,
        IReadOnlyList<(double[] Features, double Target)> comfortSamples,
        LearningModelState? warmStart)
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
        };

        var validAnger = angerSamples.Where(s => s.Features.Length == AngerFeatureCount).ToList();
        if (validAnger.Count > 0 && validAnger.Any(s => s.Label == 1) && validAnger.Any(s => s.Label == 0))
        {
            TrainLogistic(model, validAnger);
            model.AngerPositiveSamples = validAnger.Count(s => s.Label == 1);
            model.AngerNegativeSamples = validAnger.Count(s => s.Label == 0);
            model.AngerLogLoss = Math.Round(LogLoss(model, validAnger), 4);
        }

        var validComfort = comfortSamples.Where(s => s.Features.Length == ComfortFeatureCount).ToList();
        if (validComfort.Count > 0)
        {
            TrainLinear(model, validComfort);
            model.ComfortSamples = validComfort.Count;
            model.ComfortRmse = Math.Round(Rmse(model, validComfort), 3);
        }

        return model;
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

    private static void TrainLogistic(LearningModelState model, IReadOnlyList<(double[] Features, int Label)> samples)
    {
        var w = (double[])model.AngerWeights.Clone();
        var b = model.AngerBias;
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

        model.AngerWeights = w;
        model.AngerBias = b;
    }

    private static void TrainLinear(LearningModelState model, IReadOnlyList<(double[] Features, double Target)> samples)
    {
        var w = (double[])model.ComfortWeights.Clone();
        var b = model.ComfortBias;
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

        model.ComfortWeights = w;
        model.ComfortBias = b;
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

    private static double LogLoss(LearningModelState model, IReadOnlyList<(double[] Features, int Label)> samples)
    {
        var loss = 0.0;
        foreach (var (x, y) in samples)
        {
            var p = Math.Clamp(Sigmoid(Dot(model.AngerWeights, x) + model.AngerBias), 1e-9, 1 - 1e-9);
            loss += -((y * Math.Log(p)) + ((1 - y) * Math.Log(1 - p)));
        }

        return loss / samples.Count;
    }

    private static double Rmse(LearningModelState model, IReadOnlyList<(double[] Features, double Target)> samples)
    {
        var sum = 0.0;
        foreach (var (x, y) in samples)
        {
            var error = (Dot(model.ComfortWeights, x) + model.ComfortBias) - y;
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

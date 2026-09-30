using lumibelle.Models;

namespace lumibelle.Services.AI;

/// <summary>Token rate from ComfyUI's determinate "tokens" progress; restarts when a new generation begins.</summary>
internal sealed class TokenRateTracker
{
    private double? _firstValue, _lastValue;
    private TimeSpan _firstElapsed, _lastElapsed;
    public int? GeneratedTokens => _lastValue is { } value ? Math.Max(0, (int)Math.Round(value)) : null;
    public double ObservedSeconds => _firstValue is null ? 0 : (_lastElapsed - _firstElapsed).TotalSeconds;
    public double? TokensPerSecond
    {
        get
        {
            if (_firstValue is null || _lastValue is null) return null;
            var seconds = (_lastElapsed - _firstElapsed).TotalSeconds;
            var generated = _lastValue.Value - _firstValue.Value;
            return seconds > 0 && generated > 0 ? generated / seconds : null;
        }
    }
    public void Observe(GenerationProgress progress)
    {
        if (!progress.IsDeterminate || !string.Equals(progress.Unit, "tokens", StringComparison.OrdinalIgnoreCase) || progress.Current is null) return;
        var current = progress.Current.Value;
        if (_lastValue is not null && current < _lastValue)
        {
            _firstValue = null;
            _lastValue = null;
        }
        if (_lastValue is not null && current <= _lastValue) return;
        if (_firstValue is null && current > 0)
        {
            _firstValue = current;
            _firstElapsed = progress.Elapsed;
        }
        _lastValue = current;
        _lastElapsed = progress.Elapsed;
    }
}

/// <summary>
/// Notices when a ComfyUI text generation writes far slower than the model did in its test. With dynamic VRAM loading an
/// oversized request does not fail: ComfyUI streams the weights from system RAM for every token, which is many times slower.
/// </summary>
public sealed class ComfyGenerationWatch(double? expectedTokensPerSecond)
{
    /// <summary>Below this fraction of the tested speed, a generation counts as slow.</summary>
    public const double SlowFraction = 0.35;
    private const double WindowSeconds = 20;
    // Reading even a long prompt takes seconds when the weights are on the GPU.
    private static readonly TimeSpan FirstTokenLimit = TimeSpan.FromMinutes(2);
    private readonly TokenRateTracker _rate = new();
    private TimeSpan? _generationStarted;
    private bool _writing;

    public static bool IsSlow(double? observed, double? expected) => observed is { } rate && expected is { } tested && rate < tested * SlowFraction;

    /// <summary>The tested speed of the newest standard benchmark for this model and server, or null before a test.</summary>
    public static double? ExpectedTokensPerSecond(TextModelReference model, AiSettings settings) =>
        TextModelPolicy.Verification(model, settings)?.Benchmarks?.Where(b => !b.CustomPrompt && b.TokensPerSecond is not null)
            .MaxBy(b => b.MeasuredUtc)?.TokensPerSecond;

    /// <summary>Observes progress; returns a notice while the generation is slow, otherwise null.</summary>
    public string? Observe(GenerationProgress progress)
    {
        _rate.Observe(progress);
        // The text workflow's TextGenerate node is stage 2; each step of a two-step composition starts it again.
        if (progress.ExecutionStageId != "2") { _generationStarted = null; _writing = false; }
        else
        {
            _generationStarted ??= progress.Elapsed;
            _writing |= progress.IsDeterminate && progress.Current > 0;
            if (!_writing && expectedTokensPerSecond is not null && progress.Elapsed - _generationStarted >= FirstTokenLimit)
                return $"Still reading the prompt after {(progress.Elapsed - _generationStarted.Value).TotalMinutes:N0} minutes. " +
                    "ComfyUI is probably streaming the model from system RAM because this request does not fit in GPU memory, or the GPU is busy with other work. " +
                    "Cancel and make the request smaller, or wait.";
        }
        if (_rate.ObservedSeconds < WindowSeconds || !IsSlow(_rate.TokensPerSecond, expectedTokensPerSecond)) return null;
        return $"Writing at {_rate.TokensPerSecond:N1} tokens/s, far below the {expectedTokensPerSecond:N1} this model reached in its test. " +
            "ComfyUI is probably streaming the model from system RAM because this request does not fit in GPU memory, or the GPU is busy with other work. " +
            "Cancel and make the request smaller, or wait.";
    }
}

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Hive.Infrastructure.Evaluation;

internal sealed class EvaluationProjectionOptionsValidator :
    IValidateOptions<EvaluationProjectionOptions>
{
    private readonly IHostEnvironment _environment;

    public EvaluationProjectionOptionsValidator(IHostEnvironment environment)
    {
        _environment = environment ?? throw new ArgumentNullException(nameof(environment));
    }

    public ValidateOptionsResult Validate(string? name, EvaluationProjectionOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!options.Enabled)
        {
            return ValidateOptionsResult.Success;
        }

        try
        {
            var path = options.ResolveRubricPath(_environment.ContentRootPath);
            _ = BugTriageEvaluationVocabulary.Load(path);
            return ValidateOptionsResult.Success;
        }
        catch (Exception exception)
            when (exception is ArgumentException
                or InvalidDataException
                or IOException
                or UnauthorizedAccessException
                or NotSupportedException)
        {
            return ValidateOptionsResult.Fail(
                $"Evaluation projection rubric configuration is invalid: {exception.Message}");
        }
    }
}

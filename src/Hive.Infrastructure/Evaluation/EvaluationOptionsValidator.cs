using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Hive.Infrastructure.Evaluation;

internal sealed class EvaluationOptionsValidator : IValidateOptions<EvaluationOptions>
{
    private readonly IHostEnvironment _environment;

    public EvaluationOptionsValidator(IHostEnvironment environment)
    {
        _environment = environment ?? throw new ArgumentNullException(nameof(environment));
    }

    public ValidateOptionsResult Validate(string? name, EvaluationOptions options)
    {
        try
        {
            _ = EvaluationProfileCatalog.Load(options, _environment.ContentRootPath);
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
                $"Evaluation profile configuration is invalid: {exception.Message}");
        }
    }
}

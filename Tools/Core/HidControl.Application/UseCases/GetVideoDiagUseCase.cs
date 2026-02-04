using HidControl.Application.Abstractions;

namespace HidControl.Application.UseCases;

/// <summary>
/// Returns diagnostics payload for video subsystem.
/// </summary>
public sealed class GetVideoDiagUseCase
{
    private readonly IVideoDiagProvider _provider;

    /// <summary>
    /// Executes GetVideoDiagUseCase.
    /// </summary>
    /// <param name="provider">Diagnostics provider.</param>
    public GetVideoDiagUseCase(IVideoDiagProvider provider)
    {
        _provider = provider;
    }

    /// <summary>
    /// Executes the use case.
    /// </summary>
    /// <returns>Diagnostics payload.</returns>
    public object Execute()
    {
        return _provider.Get();
    }
}

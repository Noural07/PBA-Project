using System.Collections.Generic;
using Pba.Shared.Contracts.V1;

namespace IngestionService.Pipeline;

/// <summary>
/// Resultatet af én kørsel af <see cref="MeasurementNormalizer"/>. Indeholder
/// både det aggregerede <see cref="MeasurementReceived"/>-event og listen af
/// individuelle <see cref="OperatorCommentRegistered"/>-events, der skal
/// publiceres parallelt med batch-aggregatet.
/// </summary>
public sealed record NormalizationResult(
    MeasurementReceived Measurement,
    IReadOnlyList<OperatorCommentRegistered> OperatorComments,
    IReadOnlyList<string> DataAnomalies);

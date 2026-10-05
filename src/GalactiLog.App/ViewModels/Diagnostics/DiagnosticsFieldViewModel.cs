namespace GalactiLog.App.ViewModels.Diagnostics;

/// <summary>
/// One labelled readout on spec 12.8's Diagnostics page. Every value is already display text:
/// bytes, counts, timestamps and durations are formatted when the field is built, by
/// <c>MetricText</c> where it already owns the rule, so the page carries no converters.
/// </summary>
/// <param name="Label">The field name, as spec 12.8's table words it.</param>
/// <param name="Value">The rendered value. Never null and never an empty string: a value that is
/// genuinely absent renders <see cref="DiagnosticsViewModel.Unavailable"/>, because an empty cell
/// reads as a rendering bug rather than as an absent figure.</param>
public sealed record DiagnosticsFieldViewModel(string Label, string Value);

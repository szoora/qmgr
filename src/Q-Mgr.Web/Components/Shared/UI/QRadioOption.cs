namespace QMgr.Web.Components.Shared.UI;

/// <summary>One option of a <see cref="QRadioGroup{TValue}"/>. <paramref name="Description"/>, when given, reads as
/// "<b>Label</b> — description", which is how every option list with explanations in this app was written by hand.</summary>
public sealed record QRadioOption<TValue>(TValue Value, string Label, string? Description = null, bool Disabled = false);

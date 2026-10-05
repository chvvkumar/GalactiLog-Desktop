using System.Linq.Expressions;
using System.Reflection;
using Avalonia.Headless.XUnit;
using GalactiLog.App.ViewModels.Analysis;
using Xunit;

namespace GalactiLog.App.Tests.Theme;

// rc5.4's CartesianChartEngine.Measure throws "XAxes and YAxes must contain at least one element"
// for every series type, and the control measures while it is collapsed. A chart view-model whose
// empty state publishes no axes therefore takes the whole page down, not just its own tab. Every
// chart view-model documents the rule and enforces it by hand in its own empty-state method; this
// is the one place that checks all of them together and fails closed on a chart it has never seen.
//
// The rule found here, not five separate assertions: every public type in
// GalactiLog.App.ViewModels.Analysis whose name ends "ChartViewModel" is reflected, constructed and
// read for its axes fresh and after Clear(). A type this file cannot construct is an offender
// rather than a skip, so a later chart whose constructor takes a shape this file does not yet
// handle fails the census instead of shipping silently uncovered.
public class ChartEmptyAxisCensusTest
{
    [AvaloniaFact]
    public void EveryChartViewModel_PublishesExactlyOneAxisPerSide_FreshAndAfterClear()
    {
        var types = ChartViewModelTypes();
        Assert.True(
            types.Count >= 5,
            $"Only {types.Count} *ChartViewModel types were found in "
            + "GalactiLog.App.ViewModels.Analysis.");

        var offenders = new List<string>();
        foreach (var type in types)
        {
            object instance;
            try
            {
                instance = Construct(type);
            }
            catch (Exception ex)
            {
                offenders.Add($"{type.Name}: could not be constructed ({ex.Message})");
                continue;
            }

            try
            {
                CheckAxes(type, instance, "a fresh instance", offenders);

                var clear = type.GetMethod("Clear", Type.EmptyTypes);
                if (clear is null)
                {
                    offenders.Add($"{type.Name}: has no public parameterless Clear()");
                    continue;
                }

                clear.Invoke(instance, null);
                CheckAxes(type, instance, "after Clear()", offenders);
            }
            finally
            {
                (instance as IDisposable)?.Dispose();
            }
        }

        Assert.True(
            offenders.Count == 0,
            "Every chart view-model must publish exactly one XAxes entry and one YAxes entry, even "
            + "in the empty state, or rc5.4 throws while the control is collapsed. Offenders:"
            + Environment.NewLine + string.Join(Environment.NewLine, offenders));
    }

    private static void CheckAxes(Type type, object instance, string when, List<string> offenders)
    {
        CheckAxisCount(type, instance, "XAxes", when, offenders);
        CheckAxisCount(type, instance, "YAxes", when, offenders);
    }

    private static void CheckAxisCount(
        Type type, object instance, string propertyName, string when, List<string> offenders)
    {
        var property = type.GetProperty(propertyName);
        if (property is null)
        {
            offenders.Add($"{type.Name}: has no public {propertyName} property");
            return;
        }

        var count = property.GetValue(instance) is System.Collections.IEnumerable axes
            ? axes.Cast<object>().Count()
            : -1;

        if (count != 1)
        {
            offenders.Add($"{type.Name} ({when}): {propertyName} has {count} entries, expected 1");
        }
    }

    // Every public type in the Analysis view-model namespace whose name ends "ChartViewModel", so a
    // sixth chart is picked up with no edit to this file.
    private static List<Type> ChartViewModelTypes()
        => [.. typeof(CorrelationChartViewModel).Assembly.GetTypes()
            .Where(type => type.IsPublic
                && type.Namespace == typeof(CorrelationChartViewModel).Namespace
                && type.Name.EndsWith("ChartViewModel", StringComparison.Ordinal))
            .OrderBy(type => type.Name, StringComparer.Ordinal)];

    // Every chart view-model today takes either no constructor arguments or one required delegate
    // (Matrix's route out of the grid) with every other parameter optional. A required parameter
    // that is not a delegate is a construction failure and therefore an offender above, not a skip.
    private static object Construct(Type type)
    {
        var constructor = type.GetConstructors()
            .OrderBy(candidate => candidate.GetParameters().Count(parameter => !parameter.IsOptional))
            .First();

        var arguments = constructor.GetParameters()
            .Select(parameter => parameter.IsOptional
                ? parameter.DefaultValue
                : BuildDelegate(parameter.ParameterType))
            .ToArray();

        return constructor.Invoke(arguments);
    }

    // A no-op delegate matching whatever delegate type a required constructor parameter declares,
    // built from the delegate's own Invoke signature rather than by naming one delegate type here,
    // so a chart that takes a differently shaped delegate is still constructed.
    private static object BuildDelegate(Type delegateType)
    {
        if (!typeof(Delegate).IsAssignableFrom(delegateType))
        {
            throw new NotSupportedException($"{delegateType} is not a delegate type.");
        }

        var invoke = delegateType.GetMethod("Invoke")!;
        var parameters = invoke.GetParameters()
            .Select(parameter => Expression.Parameter(parameter.ParameterType, parameter.Name))
            .ToArray();
        Expression body = invoke.ReturnType == typeof(void)
            ? Expression.Empty()
            : Expression.Default(invoke.ReturnType);

        return Expression.Lambda(delegateType, body, parameters).Compile();
    }
}

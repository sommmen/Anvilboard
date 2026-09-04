using Anvilboard.Domain;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Anvilboard.Infrastructure.Persistence;

/// <summary>
/// Generic EF Core value converter that stores any <c>readonly record struct Xyz(Guid Value)</c>
/// strongly-typed id (see <c>Anvilboard.Domain.Ids</c>, all implementing
/// <see cref="IStronglyTypedId"/>) as a plain <see cref="Guid"/> column, so the domain layer never
/// has to reference EF Core to get its type-safety benefits. Construction uses a cached
/// compiled-expression factory rather than <see cref="Activator.CreateInstance(Type, object[])"/>
/// per row to keep hydration cheap.
/// </summary>
public sealed class StronglyTypedIdValueConverter<TId> : ValueConverter<TId, Guid>
    where TId : struct, IStronglyTypedId
{
    private static readonly Func<Guid, TId> Factory = BuildFactory();

    public StronglyTypedIdValueConverter()
        : base(id => id.Value, value => Factory(value))
    {
    }

    private static Func<Guid, TId> BuildFactory()
    {
        var ctor = typeof(TId).GetConstructor([typeof(Guid)])
            ?? throw new InvalidOperationException($"{typeof(TId)} must declare a constructor accepting a single Guid.");
        var parameter = System.Linq.Expressions.Expression.Parameter(typeof(Guid), "value");
        var body = System.Linq.Expressions.Expression.New(ctor, parameter);
        return System.Linq.Expressions.Expression.Lambda<Func<Guid, TId>>(body, parameter).Compile();
    }
}

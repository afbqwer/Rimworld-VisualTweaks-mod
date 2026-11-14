using System;
using System.Linq.Expressions;
using System.Reflection;
using Verse;

namespace VisualTweaks
{
    /// <summary>
    /// Turns one reflected VEF member into a box-free delegate, once, at startup.
    ///
    /// The three VEF compat files cannot reference VEF's types, so their members arrive
    /// as <see cref="FieldInfo"/>/<see cref="PropertyInfo"/> and the obvious read is
    /// <c>GetValue</c>. That boxes every value — a float read is one heap object — and
    /// costs ~25 ns; these reads run once per visible shield per frame, which makes the
    /// compat layer the only part of the draw path that allocates.
    ///
    /// <c>Delegate.CreateDelegate</c> cannot replace it: an open instance delegate needs
    /// the first parameter to be the declaring type, and the declaring type is a runtime
    /// type this assembly does not know. An expression tree can insert the cast, so it is
    /// the only shape that works without generating IL by hand. A compiled tree reads the
    /// same member in ~4 ns and allocates nothing.
    ///
    /// Every method returns null instead of throwing when the member is missing or cannot
    /// be compiled. Callers treat null exactly as they already treat a missing member:
    /// that VEF family keeps the stock drawing.
    /// </summary>
    internal static class VefMemberAccess
    {
        internal static Func<object, TValue>? CompileField<TValue>(FieldInfo? field)
        {
            if (field == null)
            {
                return null;
            }

            try
            {
                ParameterExpression instance =
                    Expression.Parameter(typeof(object), "instance");
                Expression read = Expression.Field(
                    Expression.Convert(instance, field.DeclaringType!),
                    field);
                return Expression
                    .Lambda<Func<object, TValue>>(
                        Expression.Convert(read, typeof(TValue)),
                        instance)
                    .Compile();
            }
            catch (Exception ex)
            {
                Log.Warning("[Visual Tweaks] Cannot compile a VEF field accessor ("
                    + field.DeclaringType?.Name + "." + field.Name + "): " + ex.Message);
                return null;
            }
        }

        internal static Func<object, TValue>? CompileProperty<TValue>(
            PropertyInfo? property)
        {
            if (property == null)
            {
                return null;
            }

            try
            {
                ParameterExpression instance =
                    Expression.Parameter(typeof(object), "instance");
                Expression read = Expression.Property(
                    Expression.Convert(instance, property.DeclaringType!),
                    property);
                return Expression
                    .Lambda<Func<object, TValue>>(
                        Expression.Convert(read, typeof(TValue)),
                        instance)
                    .Compile();
            }
            catch (Exception ex)
            {
                Log.Warning("[Visual Tweaks] Cannot compile a VEF property accessor ("
                    + property.DeclaringType?.Name + "." + property.Name + "): "
                    + ex.Message);
                return null;
            }
        }
    }
}

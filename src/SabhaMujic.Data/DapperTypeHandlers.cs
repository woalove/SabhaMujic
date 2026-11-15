using System.Data;
using System.Globalization;
using Dapper;

namespace SabhaMujic.Data;

/// <summary>
/// Registers Dapper type handlers for the storage conventions in <c>Migrations/0001_initial.sql</c>:
/// <see cref="Guid"/> as lowercase "D" TEXT and <see cref="DateTimeOffset"/> as INTEGER Unix milliseconds (UTC).
/// Dapper's handler registry is process-wide; registration is idempotent.
/// </summary>
internal static class DapperTypeHandlers
{
    private static readonly Lock Gate = new();
    private static bool _registered;

    public static void EnsureRegistered()
    {
        lock (Gate)
        {
            if (_registered)
            {
                return;
            }

            // Dapper maps Guid/DateTimeOffset to built-in DbTypes before consulting handlers, so the
            // built-in maps must be removed for parameters to go through the handlers.
            SqlMapper.RemoveTypeMap(typeof(Guid));
            SqlMapper.RemoveTypeMap(typeof(Guid?));
            SqlMapper.RemoveTypeMap(typeof(DateTimeOffset));
            SqlMapper.RemoveTypeMap(typeof(DateTimeOffset?));
            SqlMapper.AddTypeHandler(new GuidTextHandler());
            SqlMapper.AddTypeHandler(new UnixMillisecondsHandler());
            _registered = true;
        }
    }

    public static string ToDb(Guid value) => value.ToString("D", CultureInfo.InvariantCulture);

    public static long ToDb(DateTimeOffset value) => value.ToUnixTimeMilliseconds();

    public static DateTimeOffset FromDbTime(long value) => DateTimeOffset.FromUnixTimeMilliseconds(value);

    private sealed class GuidTextHandler : SqlMapper.TypeHandler<Guid>
    {
        public override void SetValue(IDbDataParameter parameter, Guid value)
        {
            parameter.DbType = DbType.String;
            parameter.Value = ToDb(value);
        }

        public override Guid Parse(object value) => value switch
        {
            string s => Guid.ParseExact(s, "D"),
            Guid g => g,
            _ => throw new DataException($"Cannot convert {value.GetType().Name} to Guid; GUIDs are stored as TEXT."),
        };
    }

    private sealed class UnixMillisecondsHandler : SqlMapper.TypeHandler<DateTimeOffset>
    {
        public override void SetValue(IDbDataParameter parameter, DateTimeOffset value)
        {
            parameter.DbType = DbType.Int64;
            parameter.Value = ToDb(value);
        }

        public override DateTimeOffset Parse(object value) => value switch
        {
            long l => FromDbTime(l),
            int i => FromDbTime(i),
            _ => throw new DataException(
                $"Cannot convert {value.GetType().Name} to DateTimeOffset; timestamps are stored as INTEGER Unix milliseconds."),
        };
    }
}

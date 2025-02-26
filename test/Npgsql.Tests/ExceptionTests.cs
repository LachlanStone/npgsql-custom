#region License
// The PostgreSQL License
//
// Copyright (C) 2018 The Npgsql Development Team
//
// Permission to use, copy, modify, and distribute this software and its
// documentation for any purpose, without fee, and without a written
// agreement is hereby granted, provided that the above copyright notice
// and this paragraph and the following two paragraphs appear in all copies.
//
// IN NO EVENT SHALL THE NPGSQL DEVELOPMENT TEAM BE LIABLE TO ANY PARTY
// FOR DIRECT, INDIRECT, SPECIAL, INCIDENTAL, OR CONSEQUENTIAL DAMAGES,
// INCLUDING LOST PROFITS, ARISING OUT OF THE USE OF THIS SOFTWARE AND ITS
// DOCUMENTATION, EVEN IF THE NPGSQL DEVELOPMENT TEAM HAS BEEN ADVISED OF
// THE POSSIBILITY OF SUCH DAMAGE.
//
// THE NPGSQL DEVELOPMENT TEAM SPECIFICALLY DISCLAIMS ANY WARRANTIES,
// INCLUDING, BUT NOT LIMITED TO, THE IMPLIED WARRANTIES OF MERCHANTABILITY
// AND FITNESS FOR A PARTICULAR PURPOSE. THE SOFTWARE PROVIDED HEREUNDER IS
// ON AN "AS IS" BASIS, AND THE NPGSQL DEVELOPMENT TEAM HAS NO OBLIGATIONS
// TO PROVIDE MAINTENANCE, SUPPORT, UPDATES, ENHANCEMENTS, OR MODIFICATIONS.
#endregion

using System;
using System.Data;
using System.IO;
using System.Net.Sockets;
using NUnit.Framework;
using System.Runtime.Serialization.Formatters.Binary;
using System.Threading.Tasks;

namespace Npgsql.Tests
{
    public class ExceptionTests : TestBase
    {
        [Test, Description("Generates a basic server-side exception, checks that it's properly raised and populated")]
        public void Basic()
        {
            using (var conn = OpenConnection())
            {
                // Make sure messages are in English
                conn.ExecuteNonQuery(@"SET lc_messages='en_US.UTF8'");
                conn.ExecuteNonQuery(@"
                     CREATE OR REPLACE FUNCTION pg_temp.emit_exception() RETURNS VOID AS
                        'BEGIN RAISE EXCEPTION ''testexception'' USING ERRCODE = ''12345'', DETAIL = ''testdetail''; END;'
                     LANGUAGE 'plpgsql';
                ");

                PostgresException ex = null;
                try
                {
                    conn.ExecuteNonQuery("SELECT pg_temp.emit_exception()");
                    Assert.Fail("No exception was thrown");
                }
                catch (PostgresException e)
                {
                    ex = e;
                }

                Assert.That(ex.MessageText, Is.EqualTo("testexception"));
                Assert.That(ex.Severity, Is.EqualTo("ERROR"));
                Assert.That(ex.SqlState, Is.EqualTo("12345"));
                Assert.That(ex.Position, Is.EqualTo(0));

                var data = ex.Data;
                Assert.That(data[nameof(PostgresException.Severity)], Is.EqualTo("ERROR"));
                Assert.That(data[nameof(PostgresException.SqlState)], Is.EqualTo("12345"));
                Assert.That(data.Contains(nameof(PostgresException.Position)), Is.False);

                Assert.That(conn.ExecuteScalar("SELECT 1"), Is.EqualTo(1), "Connection in bad state after an exception");
            }
        }

        [Test, Description("Ensures Detail is redacted by default in PostgresException and PostgresNotice")]
        public async Task ErrorDetailsAreRedacted()
        {
            using var conn = OpenConnection();

            await conn.ExecuteNonQueryAsync(@"
CREATE OR REPLACE FUNCTION pg_temp.raise_exception() RETURNS VOID AS $$
BEGIN
    RAISE EXCEPTION 'testexception' USING DETAIL = 'secret';
END;
$$ LANGUAGE 'plpgsql';

CREATE OR REPLACE FUNCTION pg_temp.raise_notice() RETURNS VOID AS $$
BEGIN
    RAISE NOTICE 'testexception' USING DETAIL = 'secret';
END;
$$ LANGUAGE 'plpgsql';");

            var ex = Assert.ThrowsAsync<PostgresException>(() => conn.ExecuteNonQueryAsync("SELECT * FROM pg_temp.raise_exception()"));
            Assert.That(ex.Data[nameof(PostgresException.Detail)], Does.Not.Contain("secret"));
            Assert.That(ex.ToString(), Does.Not.Contain("secret"));

            PostgresNotice notice = null;
            conn.Notice += (___, a) => notice = a.Notice;
            await conn.ExecuteNonQueryAsync("SELECT * FROM pg_temp.raise_notice()");
            Assert.That(notice.Detail, Does.Not.Contain("secret"));
        }

        [Test]
        public async Task IncludeErrorDetails()
        {
            var builder = new NpgsqlConnectionStringBuilder(ConnectionString)
            {
                Pooling = false,
                IncludeErrorDetails = true
            };
            using var conn = OpenConnection(builder);

            await conn.ExecuteNonQueryAsync($@"
CREATE OR REPLACE FUNCTION pg_temp.raise_exception() RETURNS VOID AS $$
BEGIN
    RAISE EXCEPTION 'testexception' USING DETAIL = 'secret';
END;
$$ LANGUAGE 'plpgsql';

CREATE OR REPLACE FUNCTION pg_temp.raise_notice() RETURNS VOID AS $$
BEGIN
    RAISE NOTICE 'testexception' USING DETAIL = 'secret';
END;
$$ LANGUAGE 'plpgsql';");

            var ex = Assert.ThrowsAsync<PostgresException>(() => conn.ExecuteNonQueryAsync("SELECT * FROM pg_temp.raise_exception()"));
            Assert.That(ex.Detail, Does.Contain("secret"));
            Assert.That(ex.Data[nameof(PostgresException.Detail)], Does.Contain("secret"));

            PostgresNotice notice = null;
            conn.Notice += (____, a) => notice = a.Notice;
            await conn.ExecuteNonQueryAsync("SELECT * FROM pg_temp.raise_notice()");
            Assert.That(notice.Detail, Does.Contain("secret"));
        }

        [Test]
        public void ExceptionFieldsArePopulated()
        {
            using (var conn = OpenConnection())
            {
                TestUtil.MinimumPgVersion(conn, "9.3.0", "5 error fields haven't been added yet");
                conn.ExecuteNonQuery("CREATE TEMP TABLE uniqueviolation (id INT NOT NULL, CONSTRAINT uniqueviolation_pkey PRIMARY KEY (id))");
                conn.ExecuteNonQuery("INSERT INTO uniqueviolation (id) VALUES(1)");
                try
                {
                    conn.ExecuteNonQuery("INSERT INTO uniqueviolation (id) VALUES(1)");
                }
                catch (PostgresException ex)
                {
                    Assert.That(ex.ColumnName, Is.Null, "ColumnName should not be populated for unique violations");
                    Assert.That(ex.TableName, Is.EqualTo("uniqueviolation"));
                    Assert.That(ex.SchemaName, Does.StartWith("pg_temp"));
                    Assert.That(ex.ConstraintName, Is.EqualTo("uniqueviolation_pkey"));
                    Assert.That(ex.DataTypeName, Is.Null, "DataTypeName should not be populated for unique violations");
                }
            }
        }

        [Test]
        public void ColumnNameExceptionFieldIsPopulated()
        {
            using (var conn = OpenConnection())
            {
                TestUtil.MinimumPgVersion(conn, "9.3.0", "5 error fields haven't been added yet");
                conn.ExecuteNonQuery("CREATE TEMP TABLE notnullviolation (id INT NOT NULL)");
                try
                {
                    conn.ExecuteNonQuery("INSERT INTO notnullviolation (id) VALUES(NULL)");
                }
                catch (PostgresException ex)
                {
                    Assert.That(ex.SchemaName, Does.StartWith("pg_temp"));
                    Assert.That(ex.TableName, Is.EqualTo("notnullviolation"));
                    Assert.That(ex.ColumnName, Is.EqualTo("id"));
                }
            }
        }

        [Test]
        [NonParallelizable]
        public void DataTypeNameExceptionFieldIsPopulated()
        {
            // On reading the source code for PostgreSQL9.3beta1, the only time that the
            // datatypename field is populated is when using domain types. So here we'll
            // create a domain that simply does not allow NULLs then try and cast NULL
            // to it.
            const string dropDomain = @"DROP DOMAIN IF EXISTS public.intnotnull";
            const string createDomain = @"CREATE DOMAIN public.intnotnull AS INT NOT NULL";
            const string castStatement = @"SELECT CAST(NULL AS public.intnotnull)";

            using (var conn = OpenConnection())
            {
                TestUtil.MinimumPgVersion(conn, "9.3.0", "5 error fields haven't been added yet");
                try
                {
                    var command = new NpgsqlCommand(dropDomain, conn);
                    command.ExecuteNonQuery();

                    command = new NpgsqlCommand(createDomain, conn);
                    command.ExecuteNonQuery();

                    command = new NpgsqlCommand(castStatement, conn);
                    //Cause the NOT NULL violation
                    command.ExecuteNonQuery();

                }
                catch (PostgresException ex)
                {
                    Assert.AreEqual("public", ex.SchemaName);
                    Assert.AreEqual("intnotnull", ex.DataTypeName);
                }
            }
        }

        [Test]
        public void NpgsqlExceptionInAsync()
        {
            using (var conn = OpenConnection())
            {
                Assert.That(async () => await conn.ExecuteNonQueryAsync("MALFORMED"),
                    Throws.Exception.TypeOf<PostgresException>());
                // Just in case, anything but a PostgresException would trigger the connection breaking, check that
                Assert.That(conn.FullState, Is.EqualTo(ConnectionState.Open));
            }
        }

        [Test]
        public void NpgsqlExceptionTransience()
        {
            Assert.True(new NpgsqlException("", new IOException()).IsTransient);
            Assert.True(new NpgsqlException("", new SocketException()).IsTransient);
            Assert.False(new NpgsqlException().IsTransient);
            Assert.False(new NpgsqlException("", new Exception("Inner Exception")).IsTransient);
        }

        [Test]
        public void PostgresExceptionTransience()
        {
            Assert.True(new PostgresException { SqlState = "53300" }.IsTransient);
            Assert.False(new PostgresException { SqlState = "0" }.IsTransient);
        }

#if NET451
        [Test]
        [Ignore("DbException doesn't support serialization in .NET Core 2.0 (PlatformNotSupportedException)")]
        public void Serialization()
        {
            var e = new PostgresException
            {
                Severity = "High",
                TableName = "foo",
                Position = 18
            };

            var formatter = new BinaryFormatter();
            var stream = new MemoryStream();
            formatter.Serialize(stream, e);
            stream.Seek(0, SeekOrigin.Begin);
            var e2 = (PostgresException)formatter.Deserialize(stream);

            Assert.That(e2.Severity, Is.EqualTo(e.Severity));
            Assert.That(e2.TableName, Is.EqualTo(e.TableName));
            Assert.That(e2.Position, Is.EqualTo(e.Position));
        }
#endif
    }
}

cassandra-sharp
===============
cassandra-sharp is a .NET client for Apache Cassandra. It supports (starting from version 2) only CQL binary protocol and as a consequence is only compatible with Cassandra 1.2+.
cassandra-sharp support async operations and efficient memory usage (streaming as much as it can) exposed as TPL tasks. Futures are also supported as well. Key points of cassandra-sharp are simplicity, robustness, efficiency and thread safety. Starting from version 2, cassandra-sharp is only .NET 4.0+ compatible.

cassandra-sharp supports the following features:
* async operations
* streaming support with IEnumerable
* extensible rowset mapping
* TPL integration (compatible with C# 5 async)
* robust connection handling (connection can be recovered)

If you are looking for a Thrift compatible client, or have to use Cassandra 1.0/1.1 or require .NET 3.5 support, please consider using version 0.6.4 of cassandra-sharp.



Configuration
=============
	<configSections>
		<section name="CassandraSharp" type="CassandraSharp.SectionHandler, CassandraSharp" />
	</configSections>

	<CassandraSharp>
		<Cluster name="TestCassandra">
			<Endpoints>
				<Server>localhost</Server>
			</Endpoints>
		</Cluster>
	</CassandraSharp>

Client
======
	using System;
	using System.Collections.Generic;
	using CassandraSharp;
	using CassandraSharp.CQL;
	using CassandraSharp.CQLPoco;
	using CassandraSharp.Config;

	public class SchemaKeyspaces
	{
		public bool DurableWrites { get; set; }

		public string KeyspaceName { get; set; }

		public string StrategyClass { get; set; }

		public string StrategyOptions { get; set; }
	}

	public static class Sample
	{
		private static void DisplayKeyspace(IEnumerable<SchemaKeyspaces> result)
		{
			foreach (var resKeyspace in result)
			{
				Console.WriteLine("DurableWrites={0} KeyspaceName={1} strategy_Class={2} strategy_options={3}",
									resKeyspace.DurableWrites,
									resKeyspace.KeyspaceName,
									resKeyspace.StrategyClass,
									resKeyspace.StrategyOptions);
			}
		}

		public async static Task QueryKeyspaces()
		{
			XmlConfigurator.Configure();
			using (ICluster cluster = ClusterManager.GetCluster("TestCassandra"))
			{
				const string cqlKeyspaces = "SELECT * from system.schema_keyspaces";

				// async operation
				var taskKeyspaces = cluster.Execute<SchemaKeyspaces>(cqlKeyspaces, ConsistencyLevel.QUORUM);

				// future operation meanwhile
				var futKeyspaces = cluster.Execute<SchemaKeyspaces>(cqlKeyspaces, ConsistencyLevel.QUORUM)
										  .AsFuture();

				// display the result of the async operation
				var result = await taskKeyspaces;
				DisplayKeyspace(result);

				// display the future
				DisplayKeyspace(futKeyspaces.Result);
			}
			ClusterManager.Shutdown();
		}
	}

Thanks
======
JetBrains provided a free licence of Resharper for the cassandra-sharp project. Big thanks for the awesome product.
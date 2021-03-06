﻿using System;
using System.Data.SqlClient;
using System.Threading;
using System.Transactions;
using log4net;

namespace NHibernate.Test.NHSpecificTest.NH3023
{
	public partial class DeadlockHelper
	{
		private static readonly ILog _log = LogManager.GetLogger(typeof(DeadlockHelper));

		public void ForceDeadlockOnConnection(SqlConnection connection)
		{
			using (var victimLock = new SemaphoreSlim(0))
			using (var winnerLock = new SemaphoreSlim(0))
			{
				//
				// Second thread with non-pooled connection, to deadlock
				// with current thread
				//
				Exception winnerEx = null;
				var winnerThread = new Thread(
					() =>
					{
						try
						{
							using (var scope = new TransactionScope(TransactionScopeOption.RequiresNew))
							{
								using (var cxn = new SqlConnection(connection.ConnectionString + ";Pooling=No"))
								{
									cxn.Open();
									DeadlockParticipant(cxn, false, winnerLock, victimLock);
								}
								scope.Complete();
							}
						}
						catch (Exception ex)
						{
							winnerEx = ex;
						}
					});

				winnerThread.Start();

				try
				{
					//
					// This should always throw an exception of the form
					//  Transaction (Process ID nn) was deadlocked on lock resources with another process and has been chosen as the deadlock victim. Rerun the transaction.
					//
					DeadlockParticipant(connection, true, victimLock, winnerLock);
				}
				finally
				{
					winnerThread.Join();
					if (winnerEx != null)
						_log.Warn("Winner thread failed", winnerEx);
				}

				//
				// Should never get here
				//
				_log.Warn("Expected a deadlock exception for victim, but it was not raised.");
			}
		}

		private static void DeadlockParticipant(SqlConnection connection, bool isVictim, SemaphoreSlim myLock, SemaphoreSlim partnerLock)
		{
			try
			{
				//
				// CLID = 1 has only 10 records, CLID = 3 has 100. This guarantees
				// which process will be chosen as the victim (the one which will have
				// less work to rollback)
				//
				var clid = isVictim ? 1 : 3;
				using (var cmd = new System.Data.SqlClient.SqlCommand("UPDATE DeadlockHelper SET Data = newid() WHERE CLId = @CLID", connection))
				{
					//
					// Exclusive lock on some records in the table
					//
					cmd.Parameters.AddWithValue("@CLID", clid);
					cmd.ExecuteNonQuery();
				}
			}
			finally
			{
				//
				// Notify partner that I have finished my work
				//
				myLock.Release();
			}
			//
			// Wait for partner to finish its work
			//
			if (!partnerLock.Wait(120000))
			{
				throw new InvalidOperationException("Wait for partner has taken more than two minutes");
			}

			using (var cmd = new System.Data.SqlClient.SqlCommand("SELECT TOP 1 Data FROM DeadlockHelper ORDER BY Data", connection))
			{
				//
				// Requires shared lock on table, should be blocked by
				// partner's exclusive lock
				//
				cmd.ExecuteNonQuery();
			}
		}
	}
}

﻿using IntegrationTests;
using JasperFx.Core;
using Marten;
using Marten.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Shouldly;
using Weasel.Core;
using Weasel.Postgresql;
using Weasel.Postgresql.Migrations;
using Wolverine;
using Wolverine.Marten;
using Wolverine.RDBMS;

namespace MartenTests.MultiTenancy;

public class dynamically_spin_up_new_tenant_databases_with_autocreate
{
    [Fact]
    public async Task add_databases_and_see_durability_agents_start()
    {
        var (host, store, tenantConnectionString) = await CreateHost(AutoCreate.CreateOrUpdate);
        using (host)
        {
            await host.WaitUntilAssignmentsChangeTo(w =>
            {
                w.AgentScheme = DurabilityAgent.AgentScheme;

                // 1 for the master
                w.ExpectRunningAgents(host, 1);
            }, 15.Seconds());

            var tenancy = (MasterTableTenancy)store.Options.Tenancy;
            await tenancy.AddDatabaseRecordAsync("tenant1", tenantConnectionString);

            await host.WaitUntilAssignmentsChangeTo(w =>
            {
                w.AgentScheme = DurabilityAgent.AgentScheme;

                // 1 for the master, 1 for the tenant databases
                w.ExpectRunningAgents(host, 2);
            }, 1.Minutes());

            await host.StopAsync();
        }
    }

    [Fact]
    public async Task add_databases_and_see_durability_agents_fail_to_start()
    {
        try
        {
            // This should intentionally fail because Wolverine
            // cannot create the schema objects
            _ = await CreateHost(AutoCreate.None);
        }
        catch (Exception e)
        {
            e.ShouldBeOfType<PostgresException>();
            ((PostgresException)e).MessageText.ShouldBe("relation \"wolv.wolverine_nodes\" does not exist");
        }
    }

    private static async Task<(IHost host, IDocumentStore store, string tenantConnectionString)> CreateHost(AutoCreate wolverineAutoCreate)
    {
        await using var conn = new NpgsqlConnection(Servers.PostgresConnectionString);
        await conn.OpenAsync();
        await conn.DropSchemaAsync("tenants");

        var tenantConnectionString = await CreateDatabaseIfNotExists(conn, "tenant1");

        await conn.CloseAsync();

        // Setting up a Host with Multi-tenancy
        var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                // This is too extreme for real usage, but helps tests to run faster
                opts.Durability.NodeReassignmentPollingTime = 1.Seconds();

                opts.Services.AddMarten(o =>
                    {
                        o.MultiTenantedDatabasesWithMasterDatabaseTable(_ =>
                        {
                            _.ConnectionString = Servers.PostgresConnectionString;
                            _.SchemaName = "tenants";

                            // Enable auto-creation of Master Database schema objects
                            _.AutoCreate = AutoCreate.CreateOrUpdate;
                        });

                        // Disable auto-creation of Marten schema objects
                        o.AutoCreateSchemaObjects = AutoCreate.None;
                    })
                    .IntegrateWithWolverine(
                        "wolv",
                        autoCreate: wolverineAutoCreate)

                    // All detected changes will be applied to all
                    // the configured tenant databases on startup
                    .ApplyAllDatabaseChangesOnStartup();
            })
            .StartAsync();

        var store = host.Services.GetRequiredService<IDocumentStore>();

        var tenancy = (MasterTableTenancy)store.Options.Tenancy;
        await tenancy.ClearAllDatabaseRecordsAsync();

        return (host, store, tenantConnectionString);
    }

    private static async Task<string> CreateDatabaseIfNotExists(NpgsqlConnection conn, string databaseName)
    {
        var builder = new NpgsqlConnectionStringBuilder(Servers.PostgresConnectionString);

        var exists = await conn.DatabaseExists(databaseName);
        if (!exists)
        {
            await new DatabaseSpecification().BuildDatabase(conn, databaseName);
        }
        else
        {
            // Drop Wolverine's schema because we test the creation of it
            await conn.DropSchemaAsync("wolv");
        }

        builder.Database = databaseName;

        return builder.ConnectionString;
    }
}
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Text;
using Microsoft.Xna.Framework;
using Pathoschild.Stardew.Automate.Framework.Models;
using Pathoschild.Stardew.Automate.Framework.Storage;
using Pathoschild.Stardew.Common;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Buildings;
using StardewValley.Objects;
using StardewValley.TerrainFeatures;
using SObject = StardewValley.Object;

namespace Pathoschild.Stardew.Automate.Framework
{
    /// <summary>Constructs machine groups.</summary>
    internal class MachineGroupFactory
    {
        /*********
        ** Fields
        *********/
        /// <summary>The automation factories which construct machines, containers, and connectors.</summary>
        private readonly IList<IAutomationFactory> AutomationFactories = new List<IAutomationFactory>();

        /// <summary>Get the configuration for specific machines by ID, if any.</summary>
        private readonly Func<string, ModConfigMachine?> GetMachineOverride;

        /// <summary>Build a storage manager for the given containers.</summary>
        private readonly Func<IContainer[], StorageManager> BuildStorage;

        /// <summary>Encapsulates monitoring and logging.</summary>
        private readonly IMonitor Monitor;


        /*********
        ** Public methods
        *********/
        /// <summary>Construct an instance.</summary>
        /// <param name="getMachineOverride">Get the configuration for specific machines by ID, if any.</param>
        /// <param name="buildStorage">Build a storage manager for the given containers.</param>
        /// <param name="monitor">Encapsulates monitoring and logging.</param>
        public MachineGroupFactory(Func<string, ModConfigMachine?> getMachineOverride, Func<IContainer[], StorageManager> buildStorage, IMonitor monitor)
        {
            this.GetMachineOverride = getMachineOverride;
            this.BuildStorage = buildStorage;
            this.Monitor = monitor;
        }

        /// <summary>Add an automation factory.</summary>
        /// <param name="factory">An automation factory which construct machines, containers, and connectors.</param>
        public void Add(IAutomationFactory factory)
        {
            this.AutomationFactories.Add(factory);
        }

        /// <summary>Get the unique key which identifies a location.</summary>
        /// <param name="location">The location instance.</param>
        public string GetLocationKey(GameLocation location)
        {
            return location.uniqueName.Value != null && location.uniqueName.Value != location.Name
                ? $"{location.Name} ({location.uniqueName.Value})"
                : location.Name;
        }

        /// <summary>Sort machines by priority.</summary>
        /// <param name="machines">The machines to sort.</param>
        public IEnumerable<IMachine> SortMachines(IEnumerable<IMachine> machines)
        {
            return
                (
                    from machine in machines
                    let config = this.GetMachineOverride(machine.MachineTypeID)
                    orderby config?.Priority ?? 0 descending
                    select machine
                );
        }

        /// <summary>Get all machine groups in a location.</summary>
        /// <param name="location">The location to search.</param>
        /// <param name="monitor">The monitor with which to log errors.</param>
        public IEnumerable<IMachineGroup> GetMachineGroups(GameLocation location, IMonitor monitor)
        {
            MachineGroupBuilder builder = new(this.GetLocationKey(location), this.SortMachines, this.BuildStorage);
            LocationFloodFillIndex locationIndex = new(location, monitor);
            ISet<Vector2> visited = new HashSet<Vector2>();
            foreach (Vector2 tile in location.GetTiles())
            {
                this.FloodFillGroup(builder, location, tile, locationIndex, visited);
                if (builder.HasTiles())
                {
                    yield return builder.Build();
                    builder.Reset();
                }
            }
        }

        /// <summary>Get a machine, container, or connector from the given entity, if any.</summary>
        /// <param name="location">The location to check.</param>
        /// <param name="tile">The tile to check.</param>
        /// <param name="entity">The entity to check.</param>
        public IAutomatable? GetEntityFor(GameLocation location, Vector2 tile, object entity)
        {
            return entity switch
            {
                SObject obj => this.GetEntityFor(location, tile, obj),
                TerrainFeature feature => this.GetEntityFor(location, tile, feature),
                Building building => this.GetEntityFor(location, tile, building),
                _ => null
            };
        }

        /// <summary>Get the registered automation factories.</summary>
        public IEnumerable<IAutomationFactory> GetFactories()
        {
            return this.AutomationFactories.Select(p => p);
        }


        /*********
        ** Private methods
        *********/
        /// <summary>Extend the given machine group to include all machines and containers connected to the given tile, if any.</summary>
        /// <param name="machineGroup">The machine group to extend.</param>
        /// <param name="location">The location to search.</param>
        /// <param name="origin">The first tile to check.</param>
        /// <param name="locationIndex">An indexed view of the location.</param>
        /// <param name="visited">A lookup of visited tiles.</param>
        private void FloodFillGroup(MachineGroupBuilder machineGroup, GameLocation location, in Vector2 origin, LocationFloodFillIndex locationIndex, ISet<Vector2> visited)
        {
            // skip if already visited
            if (visited.Contains(origin))
                return;

            // flood-fill connected machines & containers
            Queue<Vector2> queue = new Queue<Vector2>();
            queue.Enqueue(origin);
            while (queue.Any())
            {
                // get tile
                Vector2 tile = queue.Dequeue();
                if (!visited.Add(tile))
                    continue;

                // add machines, containers, or connectors which covers this tile
                if (this.TryAddEntities(machineGroup, location, locationIndex, tile))
                {
                    foreach (Rectangle tileArea in machineGroup.NewTileAreas)
                    {
                        // mark visited
                        foreach (Vector2 cur in tileArea.GetTiles())
                            visited.Add(cur);

                        // connect entities on surrounding tiles
                        foreach (Vector2 next in tileArea.GetSurroundingTiles())
                        {
                            if (!visited.Contains(next))
                                queue.Enqueue(next);
                        }
                    }
                    machineGroup.NewTileAreas.Clear();
                }
            }
        }

        /// <summary>Add any machines, containers, or connectors on the given tile to the machine group.</summary>
        /// <param name="group">The machine group to extend.</param>
        /// <param name="location">The location to search.</param>
        /// <param name="locationIndex">An indexed view of the location.</param>
        /// <param name="tile">The tile to search.</param>
        private bool TryAddEntities(MachineGroupBuilder group, GameLocation location, LocationFloodFillIndex locationIndex, in Vector2 tile)
        {
            bool anyAdded = false;

            foreach (IAutomatable? entity in this.GetEntities(location, locationIndex, tile))
            {
                switch (entity)
                {
                    case IMachine machine:
                        if (this.GetMachineOverride(machine.MachineTypeID)?.Enabled != false)
                            group.Add(machine);
                        anyAdded = true;
                        break;

                    case IContainer container:
                        if (container.StorageAllowed() || container.TakingItemsAllowed())
                        {
                            group.Add(container);
                            anyAdded = true;
                        }
                        break;

                    default:
                        group.Add(entity.TileArea); // connector
                        anyAdded = true;
                        break;
                }
            }

            return anyAdded;
        }

        /// <summary>Get the machines, containers, or connectors on the given tile, if any.</summary>
        /// <param name="location">The location to search.</param>
        /// <param name="locationIndex">An indexed view of the location.</param>
        /// <param name="tile">The tile to search.</param>
        private IEnumerable<IAutomatable> GetEntities(GameLocation location, LocationFloodFillIndex locationIndex, Vector2 tile)
        {
            // from entities
            foreach (object target in locationIndex.GetEntities(tile))
            {
                switch (target)
                {
                    case SObject obj:
                        {
                            IAutomatable? entity = this.GetEntityFor(location, tile, obj);
                            if (entity != null)
                                yield return entity;

                            if (obj is IndoorPot pot && pot.bush.Value != null)
                            {
                                entity = this.GetEntityFor(location, tile, pot.bush.Value);
                                if (entity != null)
                                    yield return entity;
                            }
                        }
                        break;

                    case TerrainFeature feature:
                        {
                            IAutomatable? entity = this.GetEntityFor(location, tile, feature);
                            if (entity != null)
                                yield return entity;
                        }
                        break;

                    case Building building:
                        {
                            IAutomatable? entity = this.GetEntityFor(location, tile, building);
                            if (entity != null)
                                yield return entity;
                        }
                        break;
                }
            }

            // from tile position
            foreach (IAutomationFactory factory in this.AutomationFactories)
            {
                if (this.TryGetEntityWithErrorHandling(location, tile, null, factory, p => p.GetForTile(location, tile), out IAutomatable? entity))
                    yield return entity;
            }
        }

        /// <summary>Get a machine, container, or connector from the given object, if any.</summary>
        /// <param name="location">The location to search.</param>
        /// <param name="tile">The tile to search.</param>
        /// <param name="obj">The object to check.</param>
        private IAutomatable? GetEntityFor(GameLocation location, Vector2 tile, SObject obj)
        {
            foreach (IAutomationFactory factory in this.AutomationFactories)
            {
                if (this.TryGetEntityWithErrorHandling(location, tile, obj, factory, p => p.GetFor(obj, location, tile), out IAutomatable? entity))
                    return entity;
            }

            return null;
        }

        /// <summary>Get a machine, container, or connector from the given terrain feature, if any.</summary>
        /// <param name="location">The location to search.</param>
        /// <param name="tile">The tile to search.</param>
        /// <param name="feature">The terrain feature to check.</param>
        private IAutomatable? GetEntityFor(GameLocation location, Vector2 tile, TerrainFeature feature)
        {
            foreach (IAutomationFactory factory in this.AutomationFactories)
            {
                if (this.TryGetEntityWithErrorHandling(location, tile, factory, factory, p => p.GetFor(feature, location, tile), out IAutomatable? entity))
                    return entity;
            }

            return null;
        }

        /// <summary>Get a machine, container, or connector from the given building, if any.</summary>
        /// <param name="location">The location to search.</param>
        /// <param name="tile">The tile to search.</param>
        /// <param name="building">The building to check.</param>
        private IAutomatable? GetEntityFor(GameLocation location, Vector2 tile, Building building)
        {
            foreach (IAutomationFactory factory in this.AutomationFactories)
            {
                if (this.TryGetEntityWithErrorHandling(location, tile, factory, factory, p => p.GetFor(building, location, tile), out IAutomatable? entity))
                    return entity;
            }

            return null;
        }

        /// <summary>Try to get a machine, container, or connector from an automation factory with error handling.</summary>
        /// <param name="location">The location being searched.</param>
        /// <param name="tile">The tile position being searched.</param>
        /// <param name="fromEntity">The in-game entity being checked, or <c>null</c> if we're checking the title.</param>
        /// <param name="factory">The automation factory being searched.s</param>
        /// <param name="get">Get the result from the automation factory.</param>
        /// <param name="entity">The result from the automation factory, or <c>null</c> if none was found.</param>
        /// <returns>Returns whether an <paramref name="entity"/> was successfully found.</returns>
        private bool TryGetEntityWithErrorHandling(GameLocation location, Vector2 tile, object? fromEntity, IAutomationFactory factory, Func<IAutomationFactory, IAutomatable?> get, [NotNullWhen(true)] out IAutomatable? entity)
        {
            try
            {
                entity = get(factory);
                return entity != null;
            }
            catch (Exception ex)
            {
                StringBuilder error = new StringBuilder();

                if (factory.GetType() == typeof(AutomationFactory))
                    error.Append("Failed");
                else
                    error.Append("Custom automation factory [").Append(factory.GetType().FullName).Append("] failed");

                error
                    .Append(" getting machine for location [")
                    .Append(location?.NameOrUniqueName ?? location?.GetType().FullName ?? "null location")
                    .Append("] and tile (")
                    .Append(tile.X)
                    .Append(", ")
                    .Append(tile.Y)
                    .Append(")");

                if (fromEntity != null)
                {
                    switch (fromEntity)
                    {
                        case Building building:
                            error
                                .Append(" and building [")
                                .Append(building.buildingType.Value)
                                .Append(']');
                            break;

                        case SObject obj:
                            error
                                .Append(" and object [")
                                .Append(obj.QualifiedItemId)
                                .Append("] (\"")
                                .Append(obj.DisplayName)
                                .Append("\")");
                            break;

                        case Tree tree:
                            error
                                .Append(" and tree [")
                                .Append(tree.treeType.Value)
                                .Append(']');
                            break;

                        case FruitTree tree:
                            error
                                .Append(" and fruit tree [")
                                .Append(tree.treeId.Value)
                                .Append(']');
                            break;

                        case TerrainFeature feature:
                            error
                                .Append(" and terrain feature type [")
                                .Append(feature.GetType().FullName)
                                .Append(']');
                            break;

                        default:
                            error
                                .Append(" and entity type [")
                                .Append(fromEntity.GetType().FullName)
                                .Append(']');
                            break;
                    }
                }

                error
                    .AppendLine(". Technical details:")
                    .Append(ex);

                this.Monitor.Log(error.ToString(), LogLevel.Error);
            }

            entity = null;
            return false;
        }
    }
}

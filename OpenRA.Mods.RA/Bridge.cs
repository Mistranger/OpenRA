#region Copyright & License Information
/*
 * Copyright 2007-2014 The OpenRA Developers (see AUTHORS)
 * This file is part of OpenRA, which is free software. It is made
 * available to you under the terms of the GNU General Public License
 * as published by the Free Software Foundation. For more information,
 * see COPYING.
 */
#endregion

using System;
using System.Collections.Generic;
using System.Linq;
using OpenRA.Effects;
using OpenRA.GameRules;
using OpenRA.Graphics;
using OpenRA.Mods.RA.Buildings;
using OpenRA.Primitives;
using OpenRA.Traits;

namespace OpenRA.Mods.RA
{
	class BridgeInfo : ITraitInfo, Requires<HealthInfo>, Requires<BuildingInfo>
	{
		public readonly bool Long = false;

		[Desc("Delay (in ticks) between repairing adjacent spans in long bridges")]
		public readonly int RepairPropagationDelay = 20;

		public readonly ushort Template = 0;
		public readonly ushort DamagedTemplate = 0;
		public readonly ushort DestroyedTemplate = 0;

		// For long bridges
		public readonly ushort DestroyedPlusNorthTemplate = 0;
		public readonly ushort DestroyedPlusSouthTemplate = 0;
		public readonly ushort DestroyedPlusBothTemplate = 0;

		public readonly string[] ShorePieces = { "br1", "br2" };
		public readonly int[] NorthOffset = null;
		public readonly int[] SouthOffset = null;
		
		[Desc("The name of the weapon to use when demolishing the bridge")]
		public readonly string DemolishWeapon = "Demolish";

		public object Create(ActorInitializer init) { return new Bridge(init.self, this); }

		public IEnumerable<Pair<ushort, float>> Templates
		{
			get
			{
				if (Template != 0)
					yield return Pair.New(Template, 1f);

				if (DamagedTemplate != 0)
					yield return Pair.New(DamagedTemplate, .5f);

				if (DestroyedTemplate != 0)
					yield return Pair.New(DestroyedTemplate, 0f);

				if (DestroyedPlusNorthTemplate != 0)
					yield return Pair.New(DestroyedPlusNorthTemplate, 0f);

				if (DestroyedPlusSouthTemplate != 0)
					yield return Pair.New(DestroyedPlusSouthTemplate, 0f);

				if (DestroyedPlusBothTemplate != 0)
					yield return Pair.New(DestroyedPlusBothTemplate, 0f);
			}
		}
	}

	class Bridge : IRender, INotifyDamageStateChanged
	{
		readonly Building building;
		readonly Bridge[] neighbours = new Bridge[2];
		readonly BridgeHut[] huts = new BridgeHut[2]; // Huts before this / first & after this / last
		readonly Health health;
		readonly Actor self;
		readonly BridgeInfo info;
		readonly string type;

		ushort template;
		Dictionary<CPos, byte> footprint;

		public BridgeHut Hut { get; internal set; }

		public Bridge(Actor self, BridgeInfo info)
		{
			this.self = self;
			health = self.Trait<Health>();
			health.RemoveOnDeath = false;
			this.info = info;
			type = self.Info.Name;
			building = self.Trait<Building>();
		}

		public Bridge Neighbour(int direction) { return neighbours[direction]; }
		public IEnumerable<Bridge> Enumerate(int direction, bool includeSelf = false)
		{
			for (var b = includeSelf ? this : neighbours[direction]; b != null; b = b.neighbours[direction])
				yield return b;
		}

		public void Do(Action<Bridge, int> action)
		{
			action(this, -1);
			for (var d = 0; d <= 1; d++)
				if (neighbours[d] != null)
					action(neighbours[d], d);
		}

		public void Create(ushort template, Dictionary<CPos, byte> footprint)
		{
			this.template = template;
			this.footprint = footprint;

			// Set the initial custom terrain types
			foreach (var c in footprint.Keys)
				self.World.Map.CustomTerrain[c] = GetTerrainType(c);
		}

		byte GetTerrainType(CPos cell)
		{
			var dx = cell - self.Location;
			var index = dx.X + self.World.TileSet.Templates[template].Size.X * dx.Y;
			return self.World.TileSet.GetTerrainIndex(new TerrainTile(template, (byte)index));
		}

		public void LinkNeighbouringBridges(World world, BridgeLayer bridges)
		{
			for (var d = 0; d <= 1; d++)
			{
				if (neighbours[d] != null)
					continue; // Already linked by reverse lookup

				var offset = d == 0 ? info.NorthOffset : info.SouthOffset;
				if (offset == null)
					continue; // End piece type

				neighbours[d] = GetNeighbor(offset, bridges);
				if (neighbours[d] != null)
					neighbours[d].neighbours[1 - d] = this; // Save reverse lookup
			}
		}

		public BridgeHut GetHut(int index)
		{
			if (huts[index] != null)
				return huts[index]; // Already found

			var n = neighbours[index];
			if (n == null)
				return huts[index] = Hut; // End piece

			return huts[index] = n.Hut ?? n.GetHut(index);
		}

		public Bridge GetNeighbor(int[] offset, BridgeLayer bridges)
		{
			if (offset == null)
				return null;

			return bridges.GetBridge(self.Location + new CVec(offset[0], offset[1]));
		}

		IRenderable[] TemplateRenderables(WorldRenderer wr, PaletteReference palette, ushort template)
		{
			var offset = FootprintUtils.CenterOffset(self.World, building.Info).Y + 1024;

			return footprint.Select(c => (IRenderable)(new SpriteRenderable(
				wr.Theater.TileSprite(new TerrainTile(template, c.Value)),
				wr.world.Map.CenterOfCell(c.Key), WVec.Zero, -offset, palette, 1f, true))).ToArray();
		}

		bool initialized;
		Dictionary<ushort, IRenderable[]> renderables;
		public IEnumerable<IRenderable> Render(Actor self, WorldRenderer wr)
		{
			if (!initialized)
			{
				var palette = wr.Palette("terrain");
				renderables = new Dictionary<ushort, IRenderable[]>();
				foreach (var t in info.Templates)
					renderables.Add(t.First, TemplateRenderables(wr, palette, t.First));

				initialized = true;
			}

			return renderables[template];
		}

		void KillUnitsOnBridge()
		{
			foreach (var c in footprint.Keys)
				foreach (var a in self.World.ActorMap.GetUnitsAt(c))
					if (a.HasTrait<IPositionable>() && !a.Trait<IPositionable>().CanEnterCell(c))
						a.Kill(self);
		}

		bool NeighbourIsDeadShore(Bridge neighbour)
		{
			return neighbour != null && info.ShorePieces.Contains(neighbour.type) && neighbour.health.IsDead;
		}

		bool LongBridgeSegmentIsDead()
		{
			// The long bridge artwork requires a hack to display correctly
			// if the adjacent shore piece is dead
			if (!info.Long)
				return health.IsDead;

			if (NeighbourIsDeadShore(neighbours[0]) || NeighbourIsDeadShore(neighbours[1]))
				return true;

			return health.IsDead;
		}

		ushort ChooseTemplate()
		{
			if (info.Long && LongBridgeSegmentIsDead())
			{
				// Long bridges have custom art for multiple segments being destroyed
				var previousIsDead = neighbours[0] != null && neighbours[0].LongBridgeSegmentIsDead();
				var nextIsDead = neighbours[1] != null && neighbours[1].LongBridgeSegmentIsDead();
				if (previousIsDead && nextIsDead)
					return info.DestroyedPlusBothTemplate;
				if (previousIsDead)
					return info.DestroyedPlusNorthTemplate;
				if (nextIsDead)
					return info.DestroyedPlusSouthTemplate;

				return info.DestroyedTemplate;
			}

			var ds = health.DamageState;
			return (ds == DamageState.Dead && info.DestroyedTemplate > 0) ? info.DestroyedTemplate :
				   (ds >= DamageState.Heavy && info.DamagedTemplate > 0) ? info.DamagedTemplate : info.Template;
		}

		bool killedUnits = false;
		void UpdateState()
		{
			var oldTemplate = template;

			template = ChooseTemplate();
			if (template == oldTemplate)
				return;

			// Update map
			foreach (var c in footprint.Keys)
				self.World.Map.CustomTerrain[c] = GetTerrainType(c);

			// If this bridge repair operation connects two pathfinding domains,
			// update the domain index.
			var domainIndex = self.World.WorldActor.TraitOrDefault<DomainIndex>();
			if (domainIndex != null)
				domainIndex.UpdateCells(self.World, footprint.Keys);

			if (LongBridgeSegmentIsDead() && !killedUnits)
			{
				killedUnits = true;
				KillUnitsOnBridge();
			}
		}

		public void Repair(Actor repairer, int direction, Action onComplete)
		{
			// Repair self
			var initialDamage = health.DamageState;
			self.World.AddFrameEndTask(w =>
			{
				if (health.IsDead)
				{
					health.Resurrect(self, repairer);
					killedUnits = false;
					KillUnitsOnBridge();
				}
				else
					health.InflictDamage(self, repairer, -health.MaxHP, null, true);
				if (direction < 0 ? neighbours[0] == null && neighbours[1] == null : Hut != null || neighbours[direction] == null)
					onComplete(); // Done if single or reached other hut
			});

			// Repair adjacent spans onto next hut or end
			if (direction >= 0 && Hut == null && neighbours[direction] != null)
			{
				var delay = initialDamage == DamageState.Undamaged || NeighbourIsDeadShore(neighbours[direction]) ?
					0 : info.RepairPropagationDelay;

				self.World.AddFrameEndTask(w => w.Add(new DelayedAction(delay, () =>
					neighbours[direction].Repair(repairer, direction, onComplete))));
			}
		}

		public void DamageStateChanged(Actor self, AttackInfo e)
		{
			Do((b, d) => b.UpdateState());

			// Need to update the neighbours neighbour to correctly
			// display the broken shore hack
			if (info.ShorePieces.Contains(type))
				for (var d = 0; d <= 1; d++)
					if (neighbours[d] != null && neighbours[d].neighbours[d] != null)
						neighbours[d].neighbours[d].UpdateState();
		}

		void AggregateDamageState(Bridge b, int d, ref DamageState damage)
		{
			if (b.health.DamageState > damage)
				damage = b.health.DamageState;
			if (b.Hut == null && b.neighbours[d] != null)
				AggregateDamageState(b.neighbours[d], d, ref damage);
		}

		// Find the worst span damage before other hut
		public DamageState AggregateDamageState()
		{
			var damage = health.DamageState;
			Do((b, d) => AggregateDamageState(b, d, ref damage));
			return damage;
		}

		public void Demolish(Actor saboteur, int direction)
		{
			var initialDamage = health.DamageState;
			self.World.AddFrameEndTask(w =>
			{
				var weapon = saboteur.World.Map.Rules.Weapons[info.DemolishWeapon.ToLowerInvariant()];

				// Use .FromPos since this actor is killed. Cannot use Target.FromActor
				weapon.Impact(Target.FromPos(self.CenterPosition), saboteur, Enumerable.Empty<int>());

				self.World.WorldActor.Trait<ScreenShaker>().AddEffect(15, self.CenterPosition, 6);
				self.Kill(saboteur);
			});

			// Destroy adjacent spans between (including) huts
			if (direction >= 0 && Hut == null && neighbours[direction] != null)
			{
				var delay = initialDamage == DamageState.Dead || NeighbourIsDeadShore(neighbours[direction]) ?
					0 : info.RepairPropagationDelay;

				self.World.AddFrameEndTask(w => w.Add(new DelayedAction(delay, () =>
					neighbours[direction].Demolish(saboteur, direction))));
			}
		}
	}
}

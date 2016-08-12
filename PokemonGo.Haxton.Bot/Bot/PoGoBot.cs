﻿using MoreLinq;
using NLog;
using NLog.Fluent;
using POGOProtos.Enums;
using POGOProtos.Inventory.Item;
using POGOProtos.Map.Fort;
using POGOProtos.Networking.Responses;
using PokemonGo.Haxton.Bot.Inventory;
using PokemonGo.Haxton.Bot.Navigation;
using PokemonGo.Haxton.Bot.Utilities;
using PokemonGo.RocketAPI.Extensions;
using RestSharp;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Device.Location;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace PokemonGo.Haxton.Bot.Bot
{
    public interface IPoGoBot
    {
        bool ShouldRecycleItems { get; set; }
        bool ShouldEvolvePokemon { get; set; }
        bool ShouldTransferPokemon { get; set; }

        List<Task> Run(CancellationToken _token);
    }

    public class PoGoBot : IPoGoBot
    {
        private static readonly Logger logger = LogManager.GetCurrentClassLogger();
        private static List<string> previousFoundSnipes = new List<string>();

        private DateTime LuckyEggUsed { get; set; }
        private readonly IPoGoNavigation _navigation;
        private readonly IPoGoInventory _inventory;
        private readonly IPoGoAsh _ash;
        private readonly IPoGoSnipe _snipe;
        private readonly IPoGoPokestop _pokestop;
        private readonly IPoGoPokemon _pokemon;
        private readonly ILogicSettings _settings;
        private CancellationToken _token;

        public bool ShouldRecycleItems { get; set; }
        public bool ShouldEvolvePokemon { get; set; }
        public bool ShouldTransferPokemon { get; set; }

        public PoGoBot(IPoGoNavigation navigation, IPoGoInventory inventory, IPoGoAsh ash, IPoGoSnipe snipe, IPoGoPokestop pokestop, IPoGoPokemon pokemon, ILogicSettings settings)
        {
            _navigation = navigation;
            _inventory = inventory;
            _ash = ash;
            _snipe = snipe;
            _pokestop = pokestop;
            _pokemon = pokemon;
            _settings = settings;

            LuckyEggUsed = DateTime.MinValue;
            _pokestops = new List<FortData>(_pokestop.Pokestops.ToList());

            ShouldTransferPokemon = _settings.TransferDuplicatePokemon;
            ShouldEvolvePokemon = _settings.EvolveAllPokemonWithEnoughCandy || _settings.EvolveAllPokemonAboveIv;
            ShouldRecycleItems = _settings.ItemRecycleFilter.Count > 0;
        }

        public List<Task> Run(CancellationToken _token)
        {
            this._token = _token;
            logger.Info("Starting bot.");

            var taskList = new List<Task>
            {
                Task
                    .Run(RecycleItemsTask, _token),
                Task
                    .Run(TransferDuplicatePokemon, _token),
                Task
                    .Run(FarmPokestopsTask, _token),
                Task.Run(UpdatePokestops, _token)
        };

            return taskList;
        }

        private List<FortData> _pokestops;

        private async Task UpdatePokestops()
        {
            while (true)
            {
                var newPokestops = _pokestop.Pokestops.ToList();
                lock (_pokestops)
                    if (newPokestops.Count > 0)
                        _pokestops = new List<FortData>(newPokestops);
                await Task.Delay(5000, _token);
            }
        }

        private async Task FarmPokestops()
        {
            if (!_pokestops.Any())
                return;

            var forts = BestForts(_pokestops);
            foreach (var fortData in forts)
            {
                await _navigation.Move(fortData, () => { });
                await _pokestop.Search(fortData);
                _pokestops.Remove(fortData);
            }

            if (_snipe.SnipeLocations.Count > 0)
            {
                await _snipe.DoSnipe();
                return;
            }
            await _ash.CatchEmAll();

            //var pokestop = _pokestops.Where(x => x.CooldownCompleteTimestampMs < DateTime.UtcNow.ToUnixTime()).OrderBy(i =>
            //    LocationUtils.CalculateDistanceInMeters(_navigation.CurrentLatitude, _navigation.CurrentLongitude, i.Latitude, i.Longitude)).First();
            //var distance = LocationUtils.CalculateDistanceInMeters(_navigation.CurrentLatitude, _navigation.CurrentLongitude, pokestop.Latitude, pokestop.Longitude);
            //if (distance > 100)
            //{
            //    var r = new Random((int)DateTime.UtcNow.Ticks);
            //    var lurePokestop = _pokestops.ElementAtOrDefault(r.Next(_pokestops.Count));
            //    if (lurePokestop != null)
            //        pokestop = lurePokestop;
            //}
            //await _navigation.Move(pokestop, async () => await _ash.CatchEmAll());
            //await _pokestop.Search(pokestop);

            //await _ash.CatchEmAll(pokestop);
            //_pokestops.Remove(pokestop);
        }

        private IOrderedEnumerable<FortData> BestForts(List<FortData> pokestops)
        {
            var fortList = new Dictionary<FortData, IOrderedEnumerable<FortData>>();
            for (var i = 0; i < _pokestops.Count; i++)
            {
                fortList.Add(pokestops[i], FortByLocation(pokestops, pokestops[i].Latitude, pokestops[i].Longitude));
            }
            return fortList.OrderByDescending(t => t.Value.Count()).FirstOrDefault().Value;
        }

        private IOrderedEnumerable<FortData> FortByLocation(List<FortData> pokestops, double lat, double lng)
        {
            return pokestops
                .Where(x => x.CooldownCompleteTimestampMs < DateTime.UtcNow.ToUnixTime())
                .Where(t => LocationUtils.CalculateDistanceInMeters(lat, lng, t.Latitude, t.Longitude) < 100)
                .OrderBy(i => i.Longitude)
                .ThenBy(i => i.Latitude);
        }

        private async Task FarmPokestopsTask()
        {
            while (!_token.IsCancellationRequested)
            {
                await FarmPokestops();
                await Task.Delay(100);
            }
            _token.ThrowIfCancellationRequested();
        }

        private async Task TransferDuplicatePokemon()
        {
            while (!_token.IsCancellationRequested && ShouldTransferPokemon)
            {
                EvolvePokemonTask();
                var duplicatePokemon = _inventory.GetDuplicatePokemonForTransfer(_settings.KeepPokemonsThatCanEvolve, _settings.PrioritizeIvOverCp, _settings.PokemonsNotToTransfer);
                foreach (var pokemonData in duplicatePokemon)
                {
                    if (pokemonData.Cp >= _settings.KeepMinCp || PokemonInfo.CalculatePokemonPerfection(pokemonData) > _settings.KeepMinIvPercentage)
                    {
                        continue;
                    }
                    logger.Info($"Transferring pokemon {pokemonData.PokemonId} with cp {pokemonData.Cp}.");
                    await _inventory.TransferPokemon(pokemonData.Id);

                    //var bestPokemon = _settings.PrioritizeIvOverCp
                    //    ? _inventory.GetBestPokemonByIv(pokemonData.PokemonId)
                    //    : _inventory.GetBestPokemonByCp(pokemonData.PokemonId)
                    //    ?? pokemonData;
                }
                await Task.Delay(30000);
            }
            _token.ThrowIfCancellationRequested();
        }

        private void EvolvePokemonTask()
        {
            if (_settings.UseLuckyEggsWhileEvolving)
            {
                LuckyEgg();
            }
            var list = _settings.PokemonsToEvolve;
            if (_settings.EvolveAllPokemonWithEnoughCandy)
            {
                list = null;
            }
            var pokemon = _inventory.GetPokemonToEvolve(list).ToList();
            pokemon.ForEach(async p =>
            {
                try
                {
                    logger.Info($"Evolving pokemon {p.PokemonId} with cp {p.Cp}.");
                    await _inventory.EvolvePokemon(p.Id);
                }
                catch (Exception ex)
                {
                    logger.Warn(ex, "Failed evolving egg");
                }
            });
        }

        private async void LuckyEgg()
        {
            if (LuckyEggUsed.AddMinutes(30) < DateTime.Now)
            {
                var inventoryContent = _inventory.Items;

                var luckyEggs = inventoryContent.Where(p => p.ItemId == ItemId.ItemLuckyEgg);
                var luckyEgg = luckyEggs.FirstOrDefault();

                if (luckyEgg == null || luckyEgg.Count <= 0)
                {
                    logger.Warn("No lucky eggs left. Could not use");
                    return;
                }
                LuckyEggUsed = DateTime.Now;
                logger.Info($"Lucky egg used. {luckyEgg.Count} remaining");
                await _inventory.UseLuckyEgg();
                await Task.Delay(2000);
            }
            logger.Info("Lucky egg not used. Still have one in effect.");
        }

        private async Task RecycleItemsTask()
        {
            while (!_token.IsCancellationRequested && ShouldRecycleItems)
            {
                var itemsToThrowAway = _inventory.GetItemsToRecycle(_settings.ItemRecycleFilter).ToList();
                itemsToThrowAway.ForEach(async x =>
                {
                    try
                    {
                        logger.Info($"Recycling item(s): {x.ItemId} x{x.Count}");
                        _inventory.RecycleItems(x.ItemId, x.Count);
                        await Task.Delay(100);
                    }
                    catch (Exception ex)
                    {
                        logger.Warn(ex, "Failed recyclying items.");
                    }
                });
                await Task.Delay(30000);
            }
            _token.ThrowIfCancellationRequested();
        }
    }
}
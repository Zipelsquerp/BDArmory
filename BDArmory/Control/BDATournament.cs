using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;
using BDArmory.Core;
using BDArmory.UI;
using BDArmory.Misc;
using KSP.Localization;

namespace BDArmory.Control
{
    // A serializable configuration for loading and saving the tournament state.
    [Serializable]
    public class RoundConfig : VesselSpawner.SpawnConfig
    {
        public RoundConfig(int round, int heat, bool completed, VesselSpawner.SpawnConfig config) : base(config) { this.round = round; this.heat = heat; this.completed = completed; SerializeTeams(); }
        public int round;
        public int heat;
        public bool completed;
        [SerializeField] private string serializedTeams;
        public void SerializeTeams()
        {
            if (teamsSpecific == null)
            {
                serializedTeams = null;
                return;
            }
            var teamStrings = new List<string>();
            foreach (var team in teamsSpecific)
            {
                teamStrings.Add("[" + string.Join(",", team) + "]");
            }
            serializedTeams = "[" + string.Join(",", teamStrings) + "]";
            craftFiles = null; // Avoid including the file list twice in the tournament.state file.
        }
        public void DeserializeTeams()
        {
            if (teamsSpecific == null) teamsSpecific = new List<List<string>>();
            else teamsSpecific.Clear();
            if (!string.IsNullOrEmpty(serializedTeams))
            {
                var teams = serializedTeams.Substring(1, serializedTeams.Length - 2).Split(new string[] { "],[" }, StringSplitOptions.RemoveEmptyEntries).Select(t => t.Trim(new char[] { '[', ']' })).ToList();
                foreach (var team in teams)
                {
                    var files = team.Split(',').ToList();
                    if (files.Count > 0)
                        teamsSpecific.Add(files);
                }
            }
            if (teamsSpecific.Count == 0) teamsSpecific = null;
        }
    }

    public enum TournamentType { FFA, Teams };

    [Serializable]
    public class TournamentState
    {
        public uint tournamentID;
        public string savegame;
        private List<string> craftFiles; // For FFA style tournaments.
        private List<List<string>> teamFiles; // For teams style tournaments.
        public int vesselCount;
        public int teamCount;
        public int teamsPerHeat;
        public int vesselsPerTeam;
        public bool fullTeams;
        public TournamentType tournamentType = TournamentType.FFA;
        [NonSerialized] public Dictionary<int, Dictionary<int, VesselSpawner.SpawnConfig>> rounds; // <Round, <Heat, SpawnConfig>>
        [NonSerialized] public Dictionary<int, HashSet<int>> completed = new Dictionary<int, HashSet<int>>();
        [NonSerialized] private List<Queue<string>> teamSpawnQueues = new List<Queue<string>>();
        private string message;

        /* Generate rounds and heats by shuffling the crafts list and breaking it into groups.
         * The last heat in a round will have fewer craft if the number of craft is not divisible by the number of vessels per heat.
         * The vessels per heat is limited to the number of available craft.
         */
        public bool Generate(string folder, int numberOfRounds, int vesselsPerHeat, int tournamentStyle)
        {
            folder ??= ""; // Sanitise null strings.
            tournamentID = (uint)DateTime.UtcNow.Subtract(new DateTime(2020, 1, 1)).TotalSeconds;
            tournamentType = TournamentType.FFA;
            var abs_folder = Path.Combine(Environment.CurrentDirectory, "AutoSpawn", folder);
            if (!Directory.Exists(abs_folder))
            {
                message = "Tournament folder (" + folder + ") containing craft files does not exist.";
                BDACompetitionMode.Instance.competitionStatus.Add(message);
                Debug.Log("[BDArmory.BDATournament]: " + message);
                return false;
            }
            craftFiles = Directory.GetFiles(abs_folder).Where(f => f.EndsWith(".craft")).ToList();
            vesselCount = craftFiles.Count;
            int fullHeatCount;
            switch (vesselsPerHeat)
            {
                case 0: // Auto
                    var autoVesselsPerHeat = OptimiseVesselsPerHeat(craftFiles.Count);
                    vesselsPerHeat = autoVesselsPerHeat.Item1;
                    fullHeatCount = Mathf.CeilToInt(craftFiles.Count / vesselsPerHeat) - autoVesselsPerHeat.Item2;
                    break;
                case 1: // Unlimited (all vessels in one heat).
                    vesselsPerHeat = craftFiles.Count;
                    fullHeatCount = 1;
                    break;
                default:
                    vesselsPerHeat = Mathf.Clamp(vesselsPerHeat, 1, craftFiles.Count);
                    fullHeatCount = craftFiles.Count / vesselsPerHeat;
                    break;
            }
            rounds = new Dictionary<int, Dictionary<int, VesselSpawner.SpawnConfig>>();
            //BEGIN CHANGES TO TOURNAMENT STYLE CASE 2
            switch (tournamentStyle)
            {
                case 0: // RNG
                    {
                        message = $"Generating {numberOfRounds} randomised rounds for tournament {tournamentID} for {vesselCount} vessels in AutoSpawn{(folder == "" ? "" : "/" + folder)}, each with {vesselsPerHeat} vessels per heat.";
                        Debug.Log("[BDArmory.BDATournament]: " + message);
                        BDACompetitionMode.Instance.competitionStatus.Add(message);
                        for (int roundIndex = 0; roundIndex < numberOfRounds; ++roundIndex)
                        {
                            craftFiles.Shuffle();
                            int vesselsThisHeat = vesselsPerHeat;
                            int count = 0;
                            List<string> selectedFiles = craftFiles.Take(vesselsThisHeat).ToList();
                            rounds.Add(rounds.Count, new Dictionary<int, VesselSpawner.SpawnConfig>());
                            int heatIndex = 0;
                            while (selectedFiles.Count > 0)
                            {
                                rounds[roundIndex].Add(rounds[roundIndex].Count, new VesselSpawner.SpawnConfig(
                                    BDArmorySettings.VESSEL_SPAWN_GEOCOORDS.x,
                                    BDArmorySettings.VESSEL_SPAWN_GEOCOORDS.y,
                                    BDArmorySettings.VESSEL_SPAWN_ALTITUDE,
                                    BDArmorySettings.VESSEL_SPAWN_DISTANCE,
                                    BDArmorySettings.VESSEL_SPAWN_DISTANCE_TOGGLE,
                                    BDArmorySettings.VESSEL_SPAWN_EASE_IN_SPEED,
                                    true, // Kill everything first.
                                    BDArmorySettings.VESSEL_SPAWN_REASSIGN_TEAMS, // Assign teams.
                                    0, // Number of teams.
                                    null, // List of team numbers.
                                    null, // List of List of teams' vessels.
                                    null, // No folder, we're going to specify the craft files.
                                    selectedFiles.ToList() // Add a copy of the craft files list.
                                ));
                                count += vesselsThisHeat;
                                vesselsThisHeat = heatIndex++ < fullHeatCount ? vesselsPerHeat : vesselsPerHeat - 1; // Take one less for the remaining heats to distribute the deficit of craft files.
                                selectedFiles = craftFiles.Skip(count).Take(vesselsThisHeat).ToList();
                            }
                        }
                        break;
                    }
                case 1: // N-choose-K
                    {
                        var nCr = N_Choose_K(vesselCount, vesselsPerHeat);
                        message = $"Generating a round-robin style tournament for {vesselCount} vessels in AutoSpawn{(folder == "" ? "" : "/" + folder)} with {vesselsPerHeat} vessels per heat and {numberOfRounds} rounds. This requires {numberOfRounds * nCr} heats.";
                        Debug.Log($"[BDArmory.BDATournament]: " + message);
                        BDACompetitionMode.Instance.competitionStatus.Add(message);
                        // Generate all combinations of vessels for a round.
                        var heatList = new List<VesselSpawner.SpawnConfig>();
                        foreach (var combination in Combinations(vesselCount, vesselsPerHeat))
                        {
                            heatList.Add(new VesselSpawner.SpawnConfig(
                                BDArmorySettings.VESSEL_SPAWN_GEOCOORDS.x,
                                BDArmorySettings.VESSEL_SPAWN_GEOCOORDS.y,
                                BDArmorySettings.VESSEL_SPAWN_ALTITUDE,
                                BDArmorySettings.VESSEL_SPAWN_DISTANCE,
                                BDArmorySettings.VESSEL_SPAWN_DISTANCE_TOGGLE,
                                BDArmorySettings.VESSEL_SPAWN_EASE_IN_SPEED,
                                true, // Kill everything first.
                                BDArmorySettings.VESSEL_SPAWN_REASSIGN_TEAMS, // Assign teams.
                                0, // Number of teams.
                                null, // List of team numbers.
                                null, // List of List of teams' vessels.
                                null, // No folder, we're going to specify the craft files.
                                combination.Select(i => craftFiles[i]).ToList() // Add a copy of the craft files list.
                            ));
                        }
                        // Populate the rounds.
                        for (int roundIndex = 0; roundIndex < numberOfRounds; ++roundIndex)
                        {
                            heatList.Shuffle(); // Randomise the playing order within each round.
                            rounds.Add(roundIndex, heatList.Select((heat, index) => new KeyValuePair<int, VesselSpawner.SpawnConfig>(index, heat)).ToDictionary(kvp => kvp.Key, kvp => kvp.Value));
                        }
                        break;
                    }
                case 2: //CAP - Combat Air Patrol - Created by Zipelsquerp
                    {
                        //required inputs:
                        //Vessels in 'AutoSpawn' folder 
                        //spawning location - given radius of 50km about 'interesting spawn locations'
                        //maximum number of current vessels - ignore all vessels on team b in routine
                        //intended outputs:
                        //randomly spawn vessels continuously within a radius from priority target until max lives per vessel is satisfied
                        //activate AI pilot, guard mode
                        //challenges:
                        //
                        message $="Generating a CAP mission with {totalEnemies} total enemies with {totalEnemiesAtOnce+totalFriendliesAtOnce} total ships in flight";
                        Debug.Log($"[BDArmory.BDATournament]: " + message);
                        
                        //get geo coords of friendly priority target, update every
                        TARGET = [BDArmorySettings.VESSEL_SPAWN_GEOCOORDS.x, BDArmorySettings.VESSEL_SPAWN_GEOCOORDS.y, BDArmorySettings.VESSEL_SPAWN_ALTITUDE;]
                                                
                        var heatList = new List<VesselSpawner.SpawnConfig>();
                        foreach (var combination in Combinations(vesselCount, vesselsPerHeat))
                        {
                            heatList.Add(new VesselSpawner.SpawnConfig(
                                BDArmorySettings.VESSEL_SPAWN_GEOCOORDS.x,
                                BDArmorySettings.VESSEL_SPAWN_GEOCOORDS.y,
                                BDArmorySettings.VESSEL_SPAWN_ALTITUDE,
                                BDArmorySettings.VESSEL_SPAWN_DISTANCE,
                                BDArmorySettings.VESSEL_SPAWN_DISTANCE_TOGGLE,
                                BDArmorySettings.VESSEL_SPAWN_EASE_IN_SPEED,
                                false, // do NOT kill everything first.
                                BDArmorySettings.VESSEL_SPAWN_REASSIGN_TEAMS, // Assign teams.
                                0, // Number of teams.
                                null, // List of team numbers.
                                null, // List of List of teams' vessels.
                                null, // No folder, we're going to specify the craft files.
                                combination.Select(i => craftFiles[i]).ToList() // Add a copy of the craft files list.
                            ));
                        }
                        
                        
                    }
                default:
                    throw new ArgumentOutOfRangeException("tournamentStyle", "Invalid tournament style value - not implemented.");
            }
            teamFiles = null; // Clear the teams lists.
            return true;
        }

        /// <summary>
        /// Generate a tournament.state file for teams tournaments.
        /// </summary>
        /// <param name="folder"></param>
        /// <param name="numberOfRounds"></param>
        /// <param name="teamsPerHeat"></param>
        /// <param name="vesselsPerTeam"></param>
        /// <param name="numberOfTeams"></param>
        /// <returns></returns>
        public bool Generate(string folder, int numberOfRounds, int teamsPerHeat, int vesselsPerTeam, int numberOfTeams, int tournamentStyle)
        {
            folder ??= ""; // Sanitise null strings.
            tournamentID = (uint)DateTime.UtcNow.Subtract(new DateTime(2020, 1, 1)).TotalSeconds;
            tournamentType = TournamentType.Teams;
            var abs_folder = Path.Combine(Environment.CurrentDirectory, "AutoSpawn", folder);
            if (!Directory.Exists(abs_folder))
            {
                message = "Tournament folder (" + folder + ") containing craft files or team folders does not exist.";
                BDACompetitionMode.Instance.competitionStatus.Add(message);
                Debug.Log("[BDArmory.BDATournament]: " + message);
                return false;
            }
            if (numberOfTeams > 1) // Make teams from the files in the spawn folder.
            {
                craftFiles = Directory.GetFiles(abs_folder).Where(f => f.EndsWith(".craft")).ToList();
                if (craftFiles.Count < numberOfTeams)
                {
                    message = "Insufficient vessels in AutoSpawn" + (!string.IsNullOrEmpty(folder) ? "/" + folder : "") + " to make " + numberOfTeams + " teams.";
                    BDACompetitionMode.Instance.competitionStatus.Add(message);
                    Debug.Log("[BDArmory.BDATournament]: " + message);
                    return false;
                }
                craftFiles.Shuffle();

                int numberPerTeam = craftFiles.Count / numberOfTeams;
                int residue = craftFiles.Count - numberPerTeam * numberOfTeams;
                teamFiles = new List<List<string>>();
                for (int teamCount = 0, count = 0; teamCount < numberOfTeams; ++teamCount)
                {
                    var toTake = numberPerTeam + (teamCount < residue ? 1 : 0);
                    teamFiles.Add(craftFiles.Skip(count).Take(toTake).ToList());
                    count += toTake;
                }
            }
            else // Make teams from the folders under the spawn folder.
            {
                var teamDirs = Directory.GetDirectories(abs_folder);
                if (teamDirs.Length == 0) // Make teams from each vessel in the spawn folder.
                {
                    numberOfTeams = -1; // Flag for treating craft files as folder names.
                    craftFiles = Directory.GetFiles(abs_folder).Where(f => f.EndsWith(".craft")).ToList();
                    teamFiles = craftFiles.Select(f => new List<string> { f }).ToList();
                }
                else
                {
                    teamFiles = new List<List<string>>();
                    foreach (var teamDir in teamDirs)
                    {
                        var currentTeamFiles = Directory.GetFiles(teamDir).Where(f => f.EndsWith(".craft")).ToList();
                        if (currentTeamFiles.Count > 0)
                            teamFiles.Add(currentTeamFiles);
                    }
                    foreach (var team in teamFiles)
                        team.Shuffle();
                    craftFiles = teamFiles.SelectMany(v => v).ToList();
                }
            }
            vesselCount = craftFiles.Count;
            if (teamFiles.Count < 2)
            {
                message = $"Insufficient {(numberOfTeams != 1 ? "craft files" : "folders")} in '{Path.Combine("AutoSpawn", folder)}' to generate a tournament.";
                if (BDACompetitionMode.Instance) BDACompetitionMode.Instance.competitionStatus.Add(message);
                Debug.Log("[BDArmory.BDATournament]: " + message);
                return false;
            }
            teamCount = teamFiles.Count;
            teamsPerHeat = Mathf.Clamp(teamsPerHeat, 2, teamFiles.Count);
            this.teamsPerHeat = teamsPerHeat;
            this.vesselsPerTeam = vesselsPerTeam;
            fullTeams = BDArmorySettings.TOURNAMENT_FULL_TEAMS;
            var teamsIndex = Enumerable.Range(0, teamFiles.Count).ToList();
            teamSpawnQueues.Clear();

            int fullHeatCount = teamFiles.Count / teamsPerHeat;
            rounds = new Dictionary<int, Dictionary<int, VesselSpawner.SpawnConfig>>();
            switch (tournamentStyle)
            {
                case 0: // RNG
                    {
                        message = $"Generating {numberOfRounds} randomised rounds for tournament {tournamentID} for {teamCount} teams in AutoSpawn{(folder == "" ? "" : "/" + folder)}, each with {teamsPerHeat} teams per heat.";
                        Debug.Log("[BDArmory.BDATournament]: " + message);
                        BDACompetitionMode.Instance.competitionStatus.Add(message);
                        for (int roundIndex = 0; roundIndex < numberOfRounds; ++roundIndex)
                        {
                            teamsIndex.Shuffle();
                            int teamsThisHeat = teamsPerHeat;
                            int count = 0;
                            var selectedTeams = teamsIndex.Take(teamsThisHeat).ToList();
                            var selectedCraft = SelectTeamCraft(selectedTeams, vesselsPerTeam);
                            rounds.Add(rounds.Count, new Dictionary<int, VesselSpawner.SpawnConfig>());
                            int heatIndex = 0;
                            while (selectedTeams.Count > 0)
                            {
                                rounds[roundIndex].Add(rounds[roundIndex].Count, new VesselSpawner.SpawnConfig(
                                    BDArmorySettings.VESSEL_SPAWN_GEOCOORDS.x,
                                    BDArmorySettings.VESSEL_SPAWN_GEOCOORDS.y,
                                    BDArmorySettings.VESSEL_SPAWN_ALTITUDE,
                                    BDArmorySettings.VESSEL_SPAWN_DISTANCE,
                                    BDArmorySettings.VESSEL_SPAWN_DISTANCE_TOGGLE,
                                    BDArmorySettings.VESSEL_SPAWN_EASE_IN_SPEED,
                                    true, // Kill everything first.
                                    BDArmorySettings.VESSEL_SPAWN_REASSIGN_TEAMS, // Assign teams.
                                    numberOfTeams, // Number of teams indicator.
                                    null, //selectedCraft.Select(c => c.Count).ToList(), // Not used here.
                                    selectedCraft, // List of lists of vessels. For splitting specific vessels into specific teams.
                                    null, // No folder, we're going to specify the craft files.
                                    null // No list of craft files, we've specified them directly in selectedCraft.
                                ));
                                count += teamsThisHeat;
                                teamsThisHeat = heatIndex++ < fullHeatCount ? teamsPerHeat : teamsPerHeat - 1; // Take one less for the remaining heats to distribute the deficit of teams.
                                selectedTeams = teamsIndex.Skip(count).Take(teamsThisHeat).ToList();
                                selectedCraft = SelectTeamCraft(selectedTeams, vesselsPerTeam);
                            }
                        }
                        break;
                    }
                case 1: // N-choose-K
                    {
                        var nCr = N_Choose_K(teamCount, teamsPerHeat);
                        message = $"Generating a round-robin style tournament for {teamCount} teams in AutoSpawn{(folder == "" ? "" : "/" + folder)} with {teamsPerHeat} teams per heat and {numberOfRounds} rounds. This requires {numberOfRounds * nCr} heats.";
                        Debug.Log($"[BDArmory.BDATournament]: " + message);
                        BDACompetitionMode.Instance.competitionStatus.Add(message);
                        // Generate all combinations of teams for a round.
                        var combinations = Combinations(teamCount, teamsPerHeat);
                        // Populate the rounds.
                        for (int roundIndex = 0; roundIndex < numberOfRounds; ++roundIndex)
                        {
                            var heatList = new List<VesselSpawner.SpawnConfig>();
                            foreach (var combination in combinations)
                            {
                                var selectedCraft = SelectTeamCraft(combination.Select(i => teamsIndex[i]).ToList(), vesselsPerTeam); // Vessel selection for a team can vary between rounds if the number of vessels in a team doesn't match the vesselsPerTeam parameter.
                                heatList.Add(new VesselSpawner.SpawnConfig(
                                    BDArmorySettings.VESSEL_SPAWN_GEOCOORDS.x,
                                    BDArmorySettings.VESSEL_SPAWN_GEOCOORDS.y,
                                    BDArmorySettings.VESSEL_SPAWN_ALTITUDE,
                                    BDArmorySettings.VESSEL_SPAWN_DISTANCE,
                                    BDArmorySettings.VESSEL_SPAWN_DISTANCE_TOGGLE,
                                    BDArmorySettings.VESSEL_SPAWN_EASE_IN_SPEED,
                                    true, // Kill everything first.
                                    BDArmorySettings.VESSEL_SPAWN_REASSIGN_TEAMS, // Assign teams.
                                    numberOfTeams, // Number of teams indicator.
                                    null, //selectedCraft.Select(c => c.Count).ToList(), // Not used here.
                                    selectedCraft, // List of lists of vessels. For splitting specific vessels into specific teams.
                                    null, // No folder, we're going to specify the craft files.
                                    null // No list of craft files, we've specified them directly in selectedCraft.
                                ));
                            }
                            heatList.Shuffle(); // Randomise the playing order within each round.
                            rounds.Add(roundIndex, heatList.Select((heat, index) => new KeyValuePair<int, VesselSpawner.SpawnConfig>(index, heat)).ToDictionary(kvp => kvp.Key, kvp => kvp.Value));
                        }
                        break;
                    }
                default:
                    throw new ArgumentOutOfRangeException("tournamentStyle", "Invalid tournament style value - not implemented.");
            }
            return true;
        }

        List<List<string>> SelectTeamCraft(List<int> selectedTeams, int vesselsPerTeam)
        {
            if (teamSpawnQueues.Count == 0) // Set up the spawn queues.
            {
                foreach (var teamIndex in teamFiles)
                    teamSpawnQueues.Add(new Queue<string>());
            }

            List<List<string>> selectedCraft = new List<List<string>>();
            List<string> currentTeam = new List<string>();
            foreach (var index in selectedTeams)
            {
                if (teamSpawnQueues[index].Count < vesselsPerTeam)
                {
                    // First append craft files that aren't already in the queue.
                    var craftToAdd = teamFiles[index].Where(c => !teamSpawnQueues[index].Contains(c)).ToList();
                    craftToAdd.Shuffle();
                    foreach (var craft in craftToAdd)
                    {
                        teamSpawnQueues[index].Enqueue(craft);
                    }
                    if (BDArmorySettings.TOURNAMENT_FULL_TEAMS)
                    {
                        // Then continue to fill the queue with craft files until we have enough.
                        while (teamSpawnQueues[index].Count < vesselsPerTeam)
                        {
                            craftToAdd = teamFiles[index].ToList();
                            craftToAdd.Shuffle();
                            foreach (var craft in craftToAdd)
                            {
                                teamSpawnQueues[index].Enqueue(craft);
                            }
                        }
                    }
                }
                currentTeam.Clear();
                while (currentTeam.Count < vesselsPerTeam && teamSpawnQueues[index].Count > 0)
                {
                    currentTeam.Add(teamSpawnQueues[index].Dequeue());
                }
                selectedCraft.Add(currentTeam.ToList());
            }
            return selectedCraft;
        }

        Tuple<int, int> OptimiseVesselsPerHeat(int count)
        {
            var options = count < 11 ? new List<int> { 8, 7, 6, 5 } : new List<int> { 8, 7, 6 };
            foreach (var val in options)
            {
                if (count % val == 0)
                    return new Tuple<int, int>(val, 0);
            }
            var result = OptimiseVesselsPerHeat(count + 1);
            return new Tuple<int, int>(result.Item1, result.Item2 + 1);
        }

        public bool SaveState(string stateFile)
        {
            if (rounds == null) return true; // Nothing to save.
            try
            {
                List<string> strings = new List<string>();

                strings.Add(JsonUtility.ToJson(this));
                foreach (var round in rounds.Keys)
                    foreach (var heat in rounds[round].Keys)
                        strings.Add(JsonUtility.ToJson(new RoundConfig(round, heat, completed.ContainsKey(round) && completed[round].Contains(heat), rounds[round][heat])));

                if (!Directory.GetParent(stateFile).Exists)
                { Directory.GetParent(stateFile).Create(); }
                File.WriteAllLines(stateFile, strings);
                return true;
            }
            catch (Exception e)
            {
                Debug.LogError("[BDArmory.BDATournament]: Exception thrown in SaveState: " + e.Message + "\n" + e.StackTrace);
                return false;
            }
        }

        public bool LoadState(string stateFile)
        {
            try
            {
                if (!File.Exists(stateFile)) return false;
                var strings = File.ReadAllLines(stateFile);
                var data = JsonUtility.FromJson<TournamentState>(strings[0]);
                tournamentID = data.tournamentID;
                savegame = data.savegame;
                vesselCount = data.vesselCount;
                teamCount = data.teamCount;
                teamsPerHeat = data.teamsPerHeat;
                vesselsPerTeam = data.vesselsPerTeam;
                fullTeams = data.fullTeams;
                tournamentType = data.tournamentType;
                rounds = new Dictionary<int, Dictionary<int, VesselSpawner.SpawnConfig>>();
                completed = new Dictionary<int, HashSet<int>>();
                for (int i = 1; i < strings.Length; ++i)
                {
                    if (strings[i].Length > 0)
                    {
                        var roundConfig = JsonUtility.FromJson<RoundConfig>(strings[i]);
                        roundConfig.DeserializeTeams();
                        if (!rounds.ContainsKey(roundConfig.round)) rounds.Add(roundConfig.round, new Dictionary<int, VesselSpawner.SpawnConfig>());
                        rounds[roundConfig.round].Add(roundConfig.heat, new VesselSpawner.SpawnConfig(
                            roundConfig.latitude,
                            roundConfig.longitude,
                            roundConfig.altitude,
                            roundConfig.distance,
                            roundConfig.absDistanceOrFactor,
                            roundConfig.easeInSpeed,
                            roundConfig.killEverythingFirst,
                            roundConfig.assignTeams,
                            roundConfig.numberOfTeams,
                            roundConfig.teamCounts == null || roundConfig.teamCounts.Count == 0 ? null : roundConfig.teamCounts,
                            roundConfig.teamsSpecific == null || roundConfig.teamsSpecific.Count == 0 ? null : roundConfig.teamsSpecific,
                            roundConfig.folder,
                            roundConfig.craftFiles
                        ));
                        if (roundConfig.completed)
                        {
                            if (!completed.ContainsKey(roundConfig.round)) completed.Add(roundConfig.round, new HashSet<int>());
                            completed[roundConfig.round].Add(roundConfig.heat);
                        }
                    }
                }
                return true;
            }
            catch (Exception e)
            {
                Debug.LogError("[BDArmory.BDATournament]: " + e.Message);
                return false;
            }
        }

        #region Helper functions
        /// <summary>
        /// Calculate N-choose-K.
        /// </summary>
        /// <param name="n">N</param>
        /// <param name="k">K</param>
        /// <returns>The number of ways of choosing K unique items of a collection of N.</returns>
        public static int N_Choose_K(int n, int k)
        {
            k = Mathf.Clamp(k, 0, n);
            k = Math.Min(n, k);
            var numer = Enumerable.Range(n - k + 1, k).Aggregate(1, (acc, val) => acc * val);
            var denom = Enumerable.Range(1, k).Aggregate(1, (acc, val) => acc * val);
            return Mathf.RoundToInt(numer / denom);
        }
        /// <summary>
        /// Generate all combinations of N-choose-K.
        /// </summary>
        /// <param name="n">N</param>
        /// <param name="k">K</param>
        /// <returns>List of list of unique combinations of K indices from 0 to N-1.</returns>
        public static List<List<int>> Combinations(int n, int k)
        {
            k = Mathf.Clamp(k, 0, n);
            var combinations = new List<List<int>>();
            var temp = new List<int>();
            GenerateCombinations(ref combinations, temp, 0, n, k);
            return combinations;
        }
        /// <summary>
        /// Recursively generate all combinations of N-choose-K.
        /// Helper function.
        /// </summary>
        /// <param name="combinations">The combinations are accumulated in this list of lists.</param>
        /// <param name="temp">Temporary buffer containing current chosen values.</param>
        /// <param name="i">Current choice</param>
        /// <param name="n">N</param>
        /// <param name="k">K remaining to choose</param>
        static void GenerateCombinations(ref List<List<int>> combinations, List<int> temp, int i, int n, int k)
        {
            if (k == 0)
            {
                combinations.Add(temp.ToList()); // Take a copy otherwise C# disposes of it.
                return;
            }
            for (int j = i; j < n; ++j)
            {
                temp.Add(j);
                GenerateCombinations(ref combinations, temp, j + 1, n, k - 1);
                temp.RemoveAt(temp.Count - 1);
            }
        }
        #endregion
    }

    public enum TournamentStatus { Stopped, Running, Waiting, Completed };

    [KSPAddon(KSPAddon.Startup.Flight, false)]
    public class BDATournament : MonoBehaviour
    {
        public static BDATournament Instance;

        #region Flags and Variables
        TournamentState tournamentState;
        public const string defaultStateFile = "GameData/BDArmory/PluginData/tournament.state";
        string stateFile = defaultStateFile;//"GameData/BDArmory/PluginData/tournament.state";
        string message;
        private Coroutine runTournamentCoroutine;
        public TournamentStatus tournamentStatus = TournamentStatus.Stopped;
        public uint tournamentID = 0;
        public TournamentType tournamentType = TournamentType.FFA;
        public int numberOfRounds = 0;
        public int currentRound = 0;
        public int numberOfHeats = 0;
        public int currentHeat = 0;
        public int heatsRemaining = 0;
        public int vesselCount = 0;
        public int teamCount = 0;
        public int teamsPerHeat = 0;
        public int vesselsPerTeam = 0;
        public bool fullTeams = false;
        bool competitionStarted = false;
        public bool warpingInProgress = false;
        #endregion

        void Awake()
        {
            if (Instance)
                Destroy(Instance);
            Instance = this;
        }

        void Start()
        {
            StartCoroutine(LoadStateWhenReady());
        }

        IEnumerator LoadStateWhenReady()
        {
            while (BDACompetitionMode.Instance == null)
                yield return null;
            LoadTournamentState(); // Load the last state.
        }

        void OnDestroy()
        {
            StopTournament(); // Stop any running tournament.
            SaveTournamentState(); // Save the last state.
        }

        // Load tournament state from disk
        bool LoadTournamentState(string stateFile = "")
        {
            if (stateFile != "") this.stateFile = stateFile;
            tournamentState = new TournamentState();
            if (tournamentState.LoadState(this.stateFile))
            {
                message = "Tournament state loaded from " + this.stateFile;
                tournamentID = tournamentState.tournamentID;
                tournamentType = tournamentState.tournamentType;
                vesselCount = tournamentState.vesselCount;
                teamCount = tournamentState.teamCount;
                teamsPerHeat = tournamentState.teamsPerHeat;
                vesselsPerTeam = tournamentState.vesselsPerTeam;
                fullTeams = tournamentState.fullTeams;
                numberOfRounds = tournamentState.rounds.Count;
                numberOfHeats = numberOfRounds > 0 ? tournamentState.rounds[0].Count : 0;
                heatsRemaining = tournamentState.rounds.Select(r => r.Value.Count).Sum() - tournamentState.completed.Select(c => c.Value.Count).Sum();
            }
            else
                message = "Failed to load tournament state.";
            Debug.Log("[BDArmory.BDATournament]: " + message);
            // if (BDACompetitionMode.Instance != null)
            //     BDACompetitionMode.Instance.competitionStatus.Add(message);
            tournamentStatus = heatsRemaining > 0 ? TournamentStatus.Stopped : TournamentStatus.Completed;
            return true;
        }

        // Save tournament state to disk
        bool SaveTournamentState()
        {
            if (tournamentState.SaveState(stateFile))
                message = "Tournament state saved to " + stateFile;
            else
                message = "Failed to save tournament state.";
            Debug.Log("[BDArmory.BDATournament]: " + message);
            // if (BDACompetitionMode.Instance != null)
            //     BDACompetitionMode.Instance.competitionStatus.Add(message);
            return true;
        }

        public void SetupTournament(string folder, int rounds, int vesselsPerHeat = 0, int teamsPerHeat = 0, int vesselsPerTeam = 0, int numberOfTeams = 0, int tournamentStyle = 0, string stateFile = "")
        {
            if (stateFile != "") this.stateFile = stateFile;
            tournamentState = new TournamentState();
            if (numberOfTeams == 0) // FFA
            {
                if (!tournamentState.Generate(folder, rounds, vesselsPerHeat, tournamentStyle)) return;
            }
            else // Folders or random teams
            {
                if (!tournamentState.Generate(folder, rounds, teamsPerHeat, vesselsPerTeam, numberOfTeams, tournamentStyle)) return;
            }
            tournamentID = tournamentState.tournamentID;
            tournamentType = tournamentState.tournamentType;
            vesselCount = tournamentState.vesselCount;
            teamCount = tournamentState.teamCount;
            this.teamsPerHeat = tournamentState.teamsPerHeat;
            this.vesselsPerTeam = tournamentState.vesselsPerTeam;
            fullTeams = tournamentState.fullTeams;
            numberOfRounds = tournamentState.rounds.Count;
            numberOfHeats = numberOfRounds > 0 ? tournamentState.rounds[0].Count : 0;
            heatsRemaining = tournamentState.rounds.Select(r => r.Value.Count).Sum() - tournamentState.completed.Select(c => c.Value.Count).Sum();
            tournamentStatus = heatsRemaining > 0 ? TournamentStatus.Stopped : TournamentStatus.Completed;
            SaveTournamentState();
        }

        public void RunTournament()
        {
            tournamentState.savegame = HighLogic.SaveFolder;
            BDACompetitionMode.Instance.StopCompetition();
            VesselSpawner.Instance.CancelVesselSpawn();
            if (runTournamentCoroutine != null)
                StopCoroutine(runTournamentCoroutine);
            runTournamentCoroutine = StartCoroutine(RunTournamentCoroutine());
        }

        public void StopTournament()
        {
            if (runTournamentCoroutine != null)
            {
                StopCoroutine(runTournamentCoroutine);
                runTournamentCoroutine = null;
            }
            tournamentStatus = heatsRemaining > 0 ? TournamentStatus.Stopped : TournamentStatus.Completed;
        }

        IEnumerator RunTournamentCoroutine()
        {
            bool firstRun = true; // Whether a heat has been run yet (particularly for loading partway through a tournament).
            yield return new WaitForFixedUpdate();
            foreach (var roundIndex in tournamentState.rounds.Keys)
            {
                currentRound = roundIndex;
                foreach (var heatIndex in tournamentState.rounds[roundIndex].Keys)
                {
                    currentHeat = heatIndex;
                    if (tournamentState.completed.ContainsKey(roundIndex) && tournamentState.completed[roundIndex].Contains(heatIndex)) continue; // We've done that heat.

                    message = $"Running heat {heatIndex} of round {roundIndex} of tournament {tournamentState.tournamentID} ({heatsRemaining} heats remaining in the tournament).";
                    BDACompetitionMode.Instance.competitionStatus.Add(message);
                    Debug.Log("[BDArmory.BDATournament]: " + message);

                    int attempts = 0;
                    competitionStarted = false;
                    while (!competitionStarted && attempts++ < 3) // 3 attempts is plenty
                    {
                        tournamentStatus = TournamentStatus.Running;
                        yield return ExecuteHeat(roundIndex, heatIndex);
                        if (!competitionStarted)
                            switch (VesselSpawner.Instance.spawnFailureReason)
                            {
                                case VesselSpawner.SpawnFailureReason.None: // Successful spawning, but competition failed to start for some reason.
                                    BDACompetitionMode.Instance.competitionStatus.Add("Failed to start heat due to " + BDACompetitionMode.Instance.competitionStartFailureReason + ", trying again.");
                                    break;
                                case VesselSpawner.SpawnFailureReason.VesselLostParts: // Recoverable spawning failure.
                                    BDACompetitionMode.Instance.competitionStatus.Add("Failed to start heat due to " + VesselSpawner.Instance.spawnFailureReason + ", trying again with increased altitude.");
                                    tournamentState.rounds[roundIndex][heatIndex].altitude = Math.Min(tournamentState.rounds[roundIndex][heatIndex].altitude + 3, 10); // Increase the spawning altitude and try again.
                                    break;
                                case VesselSpawner.SpawnFailureReason.TimedOut: // Recoverable spawning failure.
                                    BDACompetitionMode.Instance.competitionStatus.Add("Failed to start heat due to " + VesselSpawner.Instance.spawnFailureReason + ", trying again.");
                                    break;
                                case VesselSpawner.SpawnFailureReason.NoTerrain: // Failed to find the terrain when ground spawning.
                                    BDACompetitionMode.Instance.competitionStatus.Add("Failed to start heat due to " + VesselSpawner.Instance.spawnFailureReason + ", trying again.");
                                    attempts = Math.Max(attempts, 2); // Try only once more.
                                    break;
                                default: // Spawning is unrecoverable.
                                    BDACompetitionMode.Instance.competitionStatus.Add("Failed to start heat due to " + VesselSpawner.Instance.spawnFailureReason + ", aborting.");
                                    attempts = 3;
                                    break;
                            }
                    }
                    if (!competitionStarted)
                    {
                        message = "Failed to run heat after 3 spawning attempts, failure reasons: " + VesselSpawner.Instance.spawnFailureReason + ", " + BDACompetitionMode.Instance.competitionStartFailureReason + ". Stopping tournament. Please fix the failure reason before continuing the tournament.";
                        Debug.Log("[BDArmory.BDATournament]: " + message);
                        BDACompetitionMode.Instance.competitionStatus.Add(message);
                        tournamentStatus = TournamentStatus.Stopped;
                        yield break;
                    }
                    firstRun = false;

                    // Register the heat as completed.
                    if (!tournamentState.completed.ContainsKey(roundIndex)) tournamentState.completed.Add(roundIndex, new HashSet<int>());
                    tournamentState.completed[roundIndex].Add(heatIndex);
                    SaveTournamentState();
                    heatsRemaining = tournamentState.rounds.Select(r => r.Value.Count).Sum() - tournamentState.completed.Select(c => c.Value.Count).Sum();

                    if (TournamentAutoResume.Instance != null && TournamentAutoResume.Instance.CheckMemoryUsage()) yield break;

                    if (tournamentState.completed[roundIndex].Count < tournamentState.rounds[roundIndex].Count)
                    {
                        // Wait a bit for any user action
                        tournamentStatus = TournamentStatus.Waiting;
                        double startTime = Planetarium.GetUniversalTime();
                        while ((Planetarium.GetUniversalTime() - startTime) < BDArmorySettings.TOURNAMENT_DELAY_BETWEEN_HEATS)
                        {
                            BDACompetitionMode.Instance.competitionStatus.Add("Waiting " + (BDArmorySettings.TOURNAMENT_DELAY_BETWEEN_HEATS - (Planetarium.GetUniversalTime() - startTime)).ToString("0") + "s, then running the next heat.");
                            yield return new WaitForSeconds(1);
                        }
                    }
                }
                if (!firstRun)
                {
                    message = "All heats in round " + roundIndex + " have been run.";
                    BDACompetitionMode.Instance.competitionStatus.Add(message);
                    Debug.Log("[BDArmory.BDATournament]: " + message);
                    if (heatsRemaining > 0)
                    {
                        if (BDArmorySettings.TOURNAMENT_TIMEWARP_BETWEEN_ROUNDS > 0)
                        {
                            BDACompetitionMode.Instance.competitionStatus.Add($"Warping ahead {BDArmorySettings.TOURNAMENT_TIMEWARP_BETWEEN_ROUNDS} mins, then running the next round.");
                            yield return WarpAhead(BDArmorySettings.TOURNAMENT_TIMEWARP_BETWEEN_ROUNDS * 60);
                        }
                        else
                        {
                            // Wait a bit for any user action
                            tournamentStatus = TournamentStatus.Waiting;
                            double startTime = Planetarium.GetUniversalTime();
                            while ((Planetarium.GetUniversalTime() - startTime) < BDArmorySettings.TOURNAMENT_DELAY_BETWEEN_HEATS)
                            {
                                BDACompetitionMode.Instance.competitionStatus.Add("Waiting " + (BDArmorySettings.TOURNAMENT_DELAY_BETWEEN_HEATS - (Planetarium.GetUniversalTime() - startTime)).ToString("0") + "s, then running the next round.");
                                yield return new WaitForSeconds(1);
                            }
                        }
                    }
                }
            }
            message = "All rounds in tournament " + tournamentState.tournamentID + " have been run.";
            BDACompetitionMode.Instance.competitionStatus.Add(message);
            Debug.Log("[BDArmory.BDATournament]: " + message);
            tournamentStatus = TournamentStatus.Completed;
        }

        IEnumerator ExecuteHeat(int roundIndex, int heatIndex)
        {
            VesselSpawner.Instance.SpawnAllVesselsOnce(tournamentState.rounds[roundIndex][heatIndex]);
            while (VesselSpawner.Instance.vesselsSpawning)
                yield return new WaitForFixedUpdate();
            if (!VesselSpawner.Instance.vesselSpawnSuccess)
            {
                tournamentStatus = TournamentStatus.Stopped;
                yield break;
            }
            yield return new WaitForFixedUpdate();

            // NOTE: runs in separate coroutine
            if (BDArmorySettings.RUNWAY_PROJECT)
            {
                switch (BDArmorySettings.RUNWAY_PROJECT_ROUND)
                {
                    case 33:
                        BDACompetitionMode.Instance.StartRapidDeployment(0);
                        break;
                    default:
                        BDACompetitionMode.Instance.StartCompetitionMode(BDArmorySettings.COMPETITION_DISTANCE);
                        break;
                }
            }
            else
                BDACompetitionMode.Instance.StartCompetitionMode(BDArmorySettings.COMPETITION_DISTANCE);
            yield return new WaitForFixedUpdate(); // Give the competition start a frame to get going.

            // start timer coroutine for the duration specified in settings UI
            var duration = Core.BDArmorySettings.COMPETITION_DURATION * 60f;
            message = "Starting " + (duration > 0 ? "a " + duration.ToString("F0") + "s" : "an unlimited") + " duration competition.";
            Debug.Log("[BDArmory.BDATournament]: " + message);
            BDACompetitionMode.Instance.competitionStatus.Add(message);
            while (BDACompetitionMode.Instance.competitionStarting || BDACompetitionMode.Instance.sequencedCompetitionStarting)
                yield return new WaitForFixedUpdate(); // Wait for the competition to actually start.
            if (!BDACompetitionMode.Instance.competitionIsActive)
            {
                var message = "Competition failed to start.";
                BDACompetitionMode.Instance.competitionStatus.Add(message);
                Debug.Log("[BDArmory.BDATournament]: " + message);
                tournamentStatus = TournamentStatus.Stopped;
                yield break;
            }
            competitionStarted = true;
            while (BDACompetitionMode.Instance.competitionIsActive) // Wait for the competition to finish.
                yield return new WaitForSeconds(1);
        }

        GameObject warpCamera;
        IEnumerator WarpAhead(double warpTimeBetweenHeats)
        {
            if (!FlightGlobals.currentMainBody.hasSolidSurface)
            {
                message = "Sorry, unable to TimeWarp without a solid surface to place the spawn probe on.";
                BDACompetitionMode.Instance.competitionStatus.Add(message);
                Debug.Log("[BDArmory.BDATournament]: " + message);
                yield return new WaitForSeconds(5f);
                yield break;
            }
            warpingInProgress = true;
            Vessel spawnProbe;
            var vesselsToKill = FlightGlobals.Vessels.ToList();
            int tries = 0;
            do
            {
                spawnProbe = VesselSpawner.Instance.SpawnSpawnProbe();
                yield return new WaitWhile(() => spawnProbe != null && (!spawnProbe.loaded || spawnProbe.packed));
                while (spawnProbe != null && FlightGlobals.ActiveVessel != spawnProbe)
                {
                    LoadedVesselSwitcher.Instance.ForceSwitchVessel(spawnProbe);
                    yield return null;
                }
            } while (++tries < 3 && spawnProbe == null);
            if (spawnProbe == null)
            {
                message = "Failed to spawn spawnProbe, aborting warp.";
                BDACompetitionMode.Instance.competitionStatus.Add(message);
                Debug.LogWarning("[BDArmory.BDATournament]: " + message);
                yield break;
            }
            var up = VectorUtils.GetUpDirection(spawnProbe.transform.position);
            var refDirection = Math.Abs(Vector3.Dot(Vector3.up, up)) < 0.71f ? Vector3.up : Vector3.forward; // Avoid that the reference direction is colinear with the local surface normal.
            spawnProbe.SetPosition(spawnProbe.transform.position - Misc.Misc.GetRadarAltitudeAtPos(spawnProbe.transform.position) * up);
            if (spawnProbe.altitude > 0) spawnProbe.Landed = true;
            else spawnProbe.Splashed = true;
            spawnProbe.SetWorldVelocity(Vector3d.zero); // Set the velocity to zero so that warp goes in high mode.
            // Kill all other vessels (including debris).
            foreach (var vessel in vesselsToKill)
                VesselSpawner.Instance.RemoveVessel(vessel);
            while (VesselSpawner.Instance.removeVesselsPending > 0) yield return null;

            // Adjust the camera for a nice view.
            if (warpCamera == null) warpCamera = new GameObject("WarpCamera");
            var cameraLocalPosition = 3f * Vector3.Cross(up, refDirection).normalized + up;
            warpCamera.SetActive(true);
            warpCamera.transform.position = spawnProbe.transform.position;
            warpCamera.transform.rotation = Quaternion.LookRotation(-cameraLocalPosition, up);
            var flightCamera = FlightCamera.fetch;
            var originalCameraParentTransform = flightCamera.transform.parent;
            var originalCameraNearClipPlane = flightCamera.mainCamera.nearClipPlane;
            flightCamera.SetTargetNone();
            flightCamera.transform.parent = warpCamera.transform;
            flightCamera.transform.localPosition = cameraLocalPosition;
            flightCamera.transform.localRotation = Quaternion.identity;
            flightCamera.SetDistance(3000f);

            var warpTo = Planetarium.GetUniversalTime() + warpTimeBetweenHeats;
            var startTime = Time.time;
            do
            {
                if (TimeWarp.WarpMode != TimeWarp.Modes.HIGH && TimeWarp.CurrentRate > 1) // Warping in low mode, abort.
                {
                    TimeWarp.fetch.CancelAutoWarp();
                    TimeWarp.SetRate(0, true, false);
                    while (TimeWarp.CurrentRate > 1) yield return null; // Wait for the warping to stop.
                    spawnProbe.SetPosition(spawnProbe.transform.position - Misc.Misc.GetRadarAltitudeAtPos(spawnProbe.transform.position) * up);
                    if (spawnProbe.altitude > 0) spawnProbe.Landed = true;
                    else spawnProbe.Splashed = true;
                    spawnProbe.SetWorldVelocity(Vector3d.zero); // Set the velocity to zero so that warp goes in high mode.
                }
                startTime = Time.time;
                while (TimeWarp.WarpMode != TimeWarp.Modes.HIGH && Time.time - startTime < 3)
                {
                    spawnProbe.SetWorldVelocity(Vector3d.zero); // Set the velocity to zero so that warp goes in high mode.
                    yield return null; // Give it a second to switch to high warp mode.
                }
                TimeWarp.fetch.WarpTo(warpTo);
                startTime = Time.time;
                while (TimeWarp.CurrentRate < 2 && Time.time - startTime < 1) yield return null; // Give it a second to get going.
            } while (TimeWarp.WarpMode != TimeWarp.Modes.HIGH && TimeWarp.CurrentRate > 1); // Warping, but not high warp, bugger. Try again. FIXME KSP isn't the focused app, it doesn't want to go into high warp!
            while (TimeWarp.CurrentRate > 1) yield return null; // Wait for the warping to stop.

            // Put the camera parent back.
            flightCamera.transform.parent = originalCameraParentTransform;
            flightCamera.mainCamera.nearClipPlane = originalCameraNearClipPlane;
            warpCamera.SetActive(false);

            warpingInProgress = false;
        }
    }

    /// <summary>
    /// A class to automatically load and resume a tournament upon starting KSP.
    /// Borrows heavily from the AutoLoadGame mod. 
    /// </summary>
    [KSPAddon(KSPAddon.Startup.MainMenu, false)]
    public class TournamentAutoResume : MonoBehaviour
    {
        public static TournamentAutoResume Instance;
        public static bool firstRun = true;
        string savesDir;
        string savegame;
        string save = "persistent";
        string game;
        bool sceneLoaded = false;

        void Awake()
        {
            if (Instance != null || !firstRun) // Only the first loaded instance gets to run.
            {
                Destroy(this);
                return;
            }
            Instance = this;
            DontDestroyOnLoad(this);
            GameEvents.onLevelWasLoadedGUIReady.Add(onLevelWasLoaded);
            savesDir = Path.Combine(KSPUtil.ApplicationRootPath, "saves");
        }

        void OnDestroy()
        {
            GameEvents.onLevelWasLoadedGUIReady.Remove(onLevelWasLoaded);
        }

        void onLevelWasLoaded(GameScenes scene)
        {
            sceneLoaded = true;
            if (scene != GameScenes.MAINMENU) return;
            if (!firstRun) return;
            firstRun = false;
            StartCoroutine(WaitForSettings());
        }

        IEnumerator WaitForSettings()
        {
            yield return new WaitForSeconds(0.5f);
            var tic = Time.realtimeSinceStartup;
            yield return new WaitUntil(() => (BDArmorySettings.ready || Time.realtimeSinceStartup - tic > 10)); // Wait until the settings are ready or timed out.
            if (BDArmorySettings.AUTO_RESUME_TOURNAMENT || BDArmorySettings.AUTO_RESUME_EVOLUTION)
            { yield return StartCoroutine(AutoResumeTournament()); }
        }

        IEnumerator AutoResumeTournament()
        {
            if (!BDArmorySettings.AUTO_RESUME_EVOLUTION) // Auto-resume evolution overrides auto-resume tournament.
            {
                // Check that there is an incomplete tournament, otherwise abort.
                bool incompleteTournament = false;
                if (File.Exists(BDATournament.defaultStateFile)) // Tournament state file exists.
                {
                    var tournamentState = new TournamentState();
                    if (!tournamentState.LoadState(BDATournament.defaultStateFile)) yield break; // Failed to load
                    savegame = Path.Combine(savesDir, tournamentState.savegame, save + ".sfs");
                    if (File.Exists(savegame) && tournamentState.rounds.Select(r => r.Value.Count).Sum() - tournamentState.completed.Select(c => c.Value.Count).Sum() > 0) // Tournament state includes the savegame and has some rounds remaining —> Let's try resuming it! 
                    {
                        incompleteTournament = true;
                        game = tournamentState.savegame;
                    }
                }
                if (!incompleteTournament) yield break;
            }
            // Load saved game.
            var tic = Time.time;
            sceneLoaded = false;
            if (!LoadGame()) yield break;
            yield return new WaitUntil(() => (sceneLoaded || Time.time - tic > 10));
            if (!sceneLoaded) { Debug.Log("[BDArmory.BDATournament]: Failed to load space center scene."); yield break; }
            // Switch to flight mode.
            sceneLoaded = false;
            FlightDriver.StartWithNewLaunch("GameData/BDArmory/craft/SpawnProbe.craft", "GameData/Squad/Flags/default.png", FlightDriver.LaunchSiteName, new VesselCrewManifest()); // This triggers an error for SpaceCenterCamera2, but I don't see how to fix it and it doesn't appear to be harmful.
            tic = Time.time;
            yield return new WaitUntil(() => (sceneLoaded || Time.time - tic > 10));
            if (!sceneLoaded) { Debug.Log("[BDArmory.BDATournament]: Failed to load flight scene."); yield break; }
            // Resume the tournament.
            yield return new WaitForSeconds(1);
            if (!BDArmorySettings.AUTO_RESUME_EVOLUTION) // Auto-resume evolution overrides auto-resume tournament.
            {
                tic = Time.time;
                yield return new WaitWhile(() => ((BDATournament.Instance == null || BDATournament.Instance.tournamentID == 0) && Time.time - tic < 10)); // Wait for the tournament to be loaded or time out.
                if (BDATournament.Instance == null || BDATournament.Instance.tournamentID == 0) yield break;
                BDArmorySetup.windowBDAToolBarEnabled = true;
                BDArmorySetup.Instance.showVesselSwitcherGUI = true;
                BDArmorySetup.Instance.showVesselSpawnerGUI = true;
                BDATournament.Instance.RunTournament();
            }
            else
            {
                BDArmorySetup.windowBDAToolBarEnabled = true;
                BDArmorySetup.Instance.showVesselSwitcherGUI = true;
                Evolution.BDAModuleEvolution evolution = Evolution.BDAModuleEvolution.Instance;
                if (evolution == null) yield break;
                evolution.StartEvolution();
            }
        }

        bool LoadGame()
        {
            var gameNode = GamePersistence.LoadSFSFile(save, game);
            if (gameNode == null)
            {
                Debug.LogWarning($"[BDArmory.BDATournament]: Unable to load the save game: {savegame}");
                return false;
            }
            KSPUpgradePipeline.Process(gameNode, game, SaveUpgradePipeline.LoadContext.SFS, OnLoadDialogPiplelineFinished, (opt, n) => Debug.LogWarning($"[BDArmory.BDATournament]: KSPUpgradePipeline finished with error: {savegame}"));
            return true;
        }

        void OnLoadDialogPiplelineFinished(ConfigNode node)
        {
            HighLogic.CurrentGame = GamePersistence.LoadGameCfg(node, game, true, false);
            if (HighLogic.CurrentGame == null) return;
            if (GamePersistence.UpdateScenarioModules(HighLogic.CurrentGame))
            {
                if (node != null)
                { GameEvents.onGameStatePostLoad.Fire(node); }
                GamePersistence.SaveGame(HighLogic.CurrentGame, save, game, SaveMode.OVERWRITE);
            }
            HighLogic.CurrentGame.startScene = GameScenes.SPACECENTER;
            HighLogic.SaveFolder = game;
            HighLogic.CurrentGame.Start();
        }

        /// <summary>
        /// Check the non-native memory usage and automatically quit if it's above the configured threshold.
        /// Note: only the managed (non-native) memory is checked, the amount of native memory may or may not be comparable to the amount of non-native memory. FIXME This needs checking in a long tournament.
        /// </summary>
        /// <returns></returns>
        public bool CheckMemoryUsage()
        {
            if ((!BDArmorySettings.AUTO_RESUME_TOURNAMENT && !BDArmorySettings.AUTO_RESUME_EVOLUTION) || BDArmorySettings.QUIT_MEMORY_USAGE_THRESHOLD > BDArmorySetup.SystemMaxMemory) return false; // Only trigger if Auto-Resume Tournaments is enabled and the Quit Memory Usage Threshold is set.
            float memoryUsage = (UnityEngine.Profiling.Profiler.GetTotalReservedMemoryLong() + UnityEngine.Profiling.Profiler.GetMonoHeapSizeLong() + UnityEngine.Profiling.Profiler.GetAllocatedMemoryForGraphicsDriver()) / (1 << 30); // In GB.
            if (memoryUsage >= BDArmorySettings.QUIT_MEMORY_USAGE_THRESHOLD)
            {
                if (BDACompetitionMode.Instance != null) BDACompetitionMode.Instance.competitionStatus.Add("Quitting in 3s due to memory usage threshold reached.");
                Debug.LogWarning($"[BDArmory.BDATournament]: Quitting KSP due to reaching Auto-Quit Memory Threshold: {memoryUsage} / {BDArmorySettings.QUIT_MEMORY_USAGE_THRESHOLD}GB");
                StartCoroutine(AutoQuit(3)); // Trigger quit in 3s to give the tournament coroutine time to stop and the message to be shown.
                return true;
            }
            return false;
        }

        /// <summary>
        /// Automatically quit KSP after a delay.
        /// </summary>
        /// <param name="delay"></param>
        /// <returns></returns>
        IEnumerator AutoQuit(float delay = 1)
        {
            yield return new WaitForSeconds(delay);
            HighLogic.LoadScene(GameScenes.MAINMENU);
            yield return new WaitForSeconds(0.5f); // Pause on the Main Menu a moment, then quit.
            Application.Quit();
        }
    }
}

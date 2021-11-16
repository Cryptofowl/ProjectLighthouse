#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Kettu;
using LBPUnion.ProjectLighthouse.Helpers;
using LBPUnion.ProjectLighthouse.Logging;
using LBPUnion.ProjectLighthouse.Types;
using LBPUnion.ProjectLighthouse.Types.Match;
using LBPUnion.ProjectLighthouse.Types.Profiles;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace LBPUnion.ProjectLighthouse.Controllers
{
    [ApiController]
    [Route("LITTLEBIGPLANETPS3_XML/")]
    [Produces("text/xml")]
    public class MatchController : ControllerBase
    {
        private readonly Database database;

        public MatchController(Database database)
        {
            this.database = database;
        }

        [HttpPost("match")]
        [Produces("text/plain")]
        public async Task<IActionResult> Match()
        {
            (User, Token)? userAndToken = await this.database.UserAndTokenFromRequest(this.Request);

            if (userAndToken == null) return this.StatusCode(403, "");

            // ReSharper disable once PossibleInvalidOperationException
            User user = userAndToken.Value.Item1;
            Token token = userAndToken.Value.Item2;

            #region Parse match data

            // Example POST /match: [UpdateMyPlayerData,["Player":"FireGamer9872"]]

            string bodyString = await new StreamReader(this.Request.Body).ReadToEndAsync();

            if (bodyString.Length == 0 || bodyString[0] != '[') return this.BadRequest();

            IMatchData? matchData;
            try
            {
                matchData = MatchHelper.Deserialize(bodyString);
            }
            catch(Exception e)
            {
                Logger.Log("Exception while parsing MatchData: " + e, LoggerLevelMatch.Instance);
                Logger.Log("Data: " + bodyString, LoggerLevelMatch.Instance);

                return this.BadRequest();
            }

            if (matchData == null)
            {
                Logger.Log("Could not parse match data: matchData is null", LoggerLevelMatch.Instance);
                Logger.Log("Data: " + bodyString, LoggerLevelMatch.Instance);
                return this.BadRequest();
            }

            Logger.Log($"Parsed match from {user.Username} (type: {matchData.GetType()})", LoggerLevelMatch.Instance);

            #endregion

            #region Update LastMatch

            LastMatch? lastMatch = await this.database.LastMatches.Where(l => l.UserId == user.UserId).FirstOrDefaultAsync();

            // below makes it not look like trash
            // ReSharper disable once ConvertIfStatementToNullCoalescingExpression
            if (lastMatch == null)
            {
                lastMatch = new LastMatch
                {
                    UserId = user.UserId,
                };
                this.database.LastMatches.Add(lastMatch);
            }

            lastMatch.Timestamp = TimestampHelper.Timestamp;

            await this.database.SaveChangesAsync();

            #endregion

            #region Process match data

            if (matchData is UpdateMyPlayerData playerData)
            {
                MatchHelper.SetUserLocation(user.UserId, token.UserLocation);

                if (playerData.RoomState != null)
                {
                    Room? room = RoomHelper.FindRoomByUser(user);
                    if (room != null && Equals(room.Host, user)) room.State = (RoomState)playerData.RoomState;
                }
            }

            if (matchData is FindBestRoom && MatchHelper.UserLocations.Count > 1)
            {
                FindBestRoomResponse? response = RoomHelper.FindBestRoom(user, token.UserLocation);

                if (response == null) return this.NotFound();

                string serialized = JsonSerializer.Serialize(response, typeof(FindBestRoomResponse));
                foreach (Player player in response.Players)
                {
                    MatchHelper.AddUserRecentlyDivedIn(user.UserId, player.User.UserId);
                }

                return this.Ok($"[{{\"StatusCode\":200}},{serialized}]");
            }

            if (matchData is CreateRoom createRoom && MatchHelper.UserLocations.Count >= 1)
            {
                List<User> users = new();
                foreach (string playerUsername in createRoom.Players)
                {
                    User? player = await this.database.Users.FirstOrDefaultAsync(u => u.Username == playerUsername);
                    // ReSharper disable once ConditionIsAlwaysTrueOrFalse
                    if (player != null)
                    {
                        users.Add(player);
                    }
                    else return this.BadRequest();
                }

                // Delete old rooms based on host
                RoomHelper.Rooms.RemoveAll(r => r.Host == user);

                // Create a new one as requested
                Room newRoom = RoomHelper.CreateRoom(users, createRoom.RoomSlot);

                // Remove players in this new room from other rooms
                foreach (Room room in RoomHelper.Rooms)
                {
                    if (room == newRoom) continue;

                    foreach (User newRoomPlayer in newRoom.Players) room.Players.RemoveAll(p => p == newRoomPlayer);
                }
            }

            #endregion

            return this.Ok("[{\"StatusCode\":200}]");
        }
    }
}
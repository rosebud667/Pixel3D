﻿// Copyright © Conatus Creative, Inc. All rights reserved.
// Licensed under the Apache 2.0 License. See LICENSE.md in the project root for license terms.
using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace Pixel3D.Audio
{
	public class AmbientSoundManager
	{
		private const int ExpireTime = 60; // 1 second of silence to stop audio
		private readonly List<FadePitchPan> pendingFadePitchPans = new List<FadePitchPan>();

		// Temporary storage during update - NOTE: These are parallel
		private readonly List<IAmbientSoundSource> pendingSources = new List<IAmbientSoundSource>();

		private float duckFade = 1f;

		private object gameState;

		// Next frame
		private Dictionary<IAmbientSoundSource, int> nextAssociations =
			new Dictionary<IAmbientSoundSource, int>(ReferenceEqualityComparer<IAmbientSoundSource>.Instance);

		private List<LiveSound> nextLiveSounds = new List<LiveSound>();

		// PERF: Convert this to struct-of-array form? (We've got a nasty O(n) lookup, but our data size is hopefully small enough that we work entirely in cache)

		// Previous Frame
		private Dictionary<IAmbientSoundSource, int> previousAssociations =
			new Dictionary<IAmbientSoundSource, int>(ReferenceEqualityComparer<IAmbientSoundSource>.Instance);

		private List<LiveSound> previousLiveSounds = new List<LiveSound>();

		public AmbientSoundManager(object gameState)
		{
			this.gameState = gameState;
		}

		public void InvalidateGameState(object newGameState)
		{
			gameState = newGameState;
			previousAssociations.Clear(); // <- All our actor instance associations just got nuked
		}

		public void AddPotentialAmbientSoundSourceToPending(object potentialAmbientSoundSource, Camera camera,
			int localPlayerBits)
		{
			if (!GameIsReceivingAmbientAudio(localPlayerBits))
				return;

            var ambientSoundSource = potentialAmbientSoundSource as IAmbientSoundSource;
			if (ambientSoundSource != null)
			{
				var ambientSound = ambientSoundSource.AmbientSound;
				if (ambientSound == null)
					return;

                FadePitchPan fpp;

				if (GetPlaybackInfoFor(
					ambientSoundSource.Bounds,
					ambientSoundSource.Position,
					ambientSoundSource.FacingLeft,
					ambientSound.radius,
					ambientSound.volume,
					ambientSound.pitch,
					ambientSound.pan,
					camera,
					gameState,
					localPlayerBits, out fpp))
				{
					// At this point, we have an audible sound. Store it. NOTE: Parallel lists
					pendingSources.Add(ambientSoundSource);
					pendingFadePitchPans.Add(fpp);
				}
			}
		}

		private bool GameIsReceivingAmbientAudio(int localPlayerBits)
		{
			// if the game is paused locally, we should quickly roll off any ambient sound sources
			return false;
			// return gameState.GetFirstLocalGameMenu(localPlayerBits) == null;
		}

		public void Update(int localPlayerBits, bool ducking)
		{
			if (!AudioDevice.Available)
			{
				pendingSources.Clear();
				pendingFadePitchPans.Clear();
				return;
			}

			var fadeAmount = ducking
				? -(1f / (SoundEffectManager.slotFadeTime * 60f))
				: 1f / (SoundEffectManager.slotFadeTime * 60f);
			duckFade = AudioMath.Clamp(duckFade + fadeAmount, 0.4f,
				1); // <- NOTE: not fading out all the way (sounds better)

			//
			// FIRST PASS: Pair sources with already-known associations:
			//

			for (var i = 0; i < pendingSources.Count;)
			{
			    int association;
				if (previousAssociations.TryGetValue(pendingSources[i], out association))
				{
					var sourceAmbientSound = pendingSources[i].AmbientSound;
					var liveSound = previousLiveSounds[association];

					if (ReferenceEquals(liveSound.soundEffect, sourceAmbientSound.soundEffect.inner) &&
					    GameIsReceivingAmbientAudio(localPlayerBits))
					{
						// Update sound
						pendingFadePitchPans[i].ApplyTo(liveSound.soundEffectInstance,
							duckFade * SafeSoundEffect.SoundEffectVolume);
						liveSound.position = pendingSources[i].Position;
						liveSound.expiryTimer = 0;

						// Transfer to next list
						previousLiveSounds[association] =
							null; // <- Prevent re-use later (cannot remove - indexes are used for associations)
						nextAssociations.Add(pendingSources[i], nextLiveSounds.Count);
						nextLiveSounds.Add(liveSound);

						// This sound source is now delt with (parallel removal)
						pendingSources.RemoveAtUnordered(i);
						pendingFadePitchPans.RemoveAtUnordered(i);
						continue; // <- in-loop removal
					}

					// Sound was changed - expire the associated sound immediately (assign a voice to the source later)
					liveSound.soundEffect = null; // <- prevent re-use in second pass
					liveSound.expiryTimer = ExpireTime; // <- flag for cleanup
				}

				i++;
			}

			previousAssociations.Clear();

			//
			// SECOND PASS: Pair remaining pending sources with remaining live sounds based on effect and distance
			//

			for (var i = 0; i < pendingSources.Count;)
			{
				var sourceAmbientSound = pendingSources[i].AmbientSound;
				var sourcePosition = pendingSources[i].Position;


				// Find the closest live sound to assign as a voice
				var bestDistanceSquared = int.MaxValue;
				var bestIndex = -1;

				for (var j = 0; j < previousLiveSounds.Count;)
				{
					if (previousLiveSounds[j] == null) // Check for removal by first pass
					{
						previousLiveSounds.RemoveAtUnordered(j); // After first pass, we can remove properly
						continue;
					}

					var liveSound = previousLiveSounds[j];

					if (ReferenceEquals(liveSound.soundEffect, sourceAmbientSound.soundEffect.inner))
					{
						var distanceSquared = Position.DistanceSquared(liveSound.position, sourcePosition);
						if (distanceSquared < bestDistanceSquared)
						{
							bestDistanceSquared = distanceSquared;
							bestIndex = j;
						}
					}

					j++;
				}


				// At this point, we've found the best available voice
				if (bestIndex != -1)
				{
					var liveSound = previousLiveSounds[bestIndex];

					// Update sound
					pendingFadePitchPans[i].ApplyTo(liveSound.soundEffectInstance,
						duckFade * SafeSoundEffect.SoundEffectVolume);
					liveSound.position = pendingSources[i].Position;
					liveSound.expiryTimer = 0;

					// Transfer to next list
					previousLiveSounds.RemoveAtUnordered(bestIndex); // After first pass, we can remove properly
					nextAssociations.Add(pendingSources[i], nextLiveSounds.Count);
					nextLiveSounds.Add(liveSound);


					// This sound source is now delt with (parallel removal)
					pendingSources.RemoveAtUnordered(i);
					pendingFadePitchPans.RemoveAtUnordered(i);
					continue; // <- in-loop removal
				}

				i++;
			}


			//
			// Silence remaining live sounds, apply expiration
			//

			foreach (var liveSound in previousLiveSounds)
			{
				// Note that we attempt to remove these in the previous loop, but that loop may not run
				if (liveSound == null)
					continue;

				liveSound.expiryTimer++;
				if (liveSound.expiryTimer > ExpireTime)
				{
					liveSound.soundEffectInstance.Dispose(); // <- this stops the sound
					// Remove from the live sound list by virtue of not adding it to the next list
				}
				else
				{
					const float
						fadeOutFrames =
							10f; // <- quickly fade out any remaining sound from ambient sounds that stop existing

					var volume = liveSound.soundEffectInstance.Volume;
					liveSound.soundEffectInstance.Volume = Math.Max(0, volume - 1f / fadeOutFrames);
					nextLiveSounds.Add(liveSound);
				}
			}

			previousLiveSounds.Clear();


			//
			// THIRD PASS: Spawn voices for remaining sound sources
			//

			for (var i = 0; i < pendingSources.Count; i++)
			{
				var ambientSound = pendingSources[i].AmbientSound;

				var sei = ambientSound.soundEffect.CreateInstance();
				pendingFadePitchPans[i].ApplyTo(sei, duckFade * SafeSoundEffect.SoundEffectVolume);
				sei.IsLooped = true;
				sei.Play();

				nextLiveSounds.Add(new LiveSound
				{
					soundEffect = ambientSound.soundEffect,
					soundEffectInstance = sei,
					position = pendingSources[i].Position,
					expiryTimer = 0
				});
			}

			pendingSources.Clear();
			pendingFadePitchPans.Clear();

			//
			// Cycle for next call
			//

			// These should have been cleared above
			Debug.Assert(pendingSources.Count == 0);
			Debug.Assert(pendingFadePitchPans.Count == 0);
			Debug.Assert(previousLiveSounds.Count == 0);
			Debug.Assert(previousAssociations.Count == 0);

			// Swaps:

			var tempLiveSounds = previousLiveSounds;
			previousLiveSounds = nextLiveSounds;
			nextLiveSounds = tempLiveSounds;

			var tempAssociations = previousAssociations;
			previousAssociations = nextAssociations;
			nextAssociations = tempAssociations;
		}

		private class LiveSound
		{
			public int expiryTimer;
			public Position position;
			public SafeSoundEffect soundEffect;
			public SafeSoundEffectInstance soundEffectInstance;
		}

		#region Spatial Playback Parameter Modulation

		/// <returns>Returns true if this ambient sound is playable</returns>
		public static bool GetPlaybackInfoFor(
			AABB? aabb,
			Position position,
			bool facingLeft,
			int radius,
			float volume,
			float pitch,
			float pan,
			Camera camera,
			object gameState,
			int localPlayerBits,
			out FadePitchPan fadePitchPan)
		{
			//
			// Global ambient audio
			//

			if (radius < 0)
			{
				fadePitchPan = new FadePitchPan(1f);
				return true;
			}

			//
			// Nominal audio position:
			//
			var worldToAudio = WorldToAudio(position, camera);
			

			fadePitchPan = new FadePitchPan(worldToAudio.pitch, worldToAudio.pan);
			fadePitchPan.fade *= volume;
			fadePitchPan.pitch *= pitch;
			fadePitchPan.pan *= pan;

			if (fadePitchPan.fade < 0.001f) // Far off-camera
			{
				fadePitchPan.fade = 0;
				return false;
			}

			//
			// Distance to a listening player:
			//

			if (localPlayerBits == 0) // No one to listen
			{
				fadePitchPan.fade = 0;
				return false;
			}

			var distanceSquared = GetDistanceSquaredToLocalPlayer(aabb, position, facingLeft, gameState, localPlayerBits);
			if (distanceSquared > radius * radius)
			{
				fadePitchPan.fade = 0;
				return false;
			}

			//
			// Modulate by listening player distance and apply settings:
			//

			// Trying linear fade-out here
			var listenerFade = AudioMath.Clamp(1f - (float) Math.Sqrt(distanceSquared) / radius, 0f, 1f);
			fadePitchPan.fade *= listenerFade;
			return true;
		}

		private static PitchPan WorldToAudio(Position position, Camera camera)
		{
			var audioPosition = camera.WorldToAudio(position);
			return new PitchPan(audioPosition.X, audioPosition.Y);
		}

		public static int GetDistanceSquaredToLocalPlayer(AABB? aabb, Position position, bool facingLeft,
			object gameState, int localPlayerBits)
		{
			var maxPlayers = AudioSystem.getMaxPlayers();

			var bestDistanceSquared = int.MaxValue;

			if (aabb != null) // Sound source has some (spatial) volume
			{
				for (var i = 0; i < maxPlayers; i++) // For each possible player
				{
					if (((1 << i) & localPlayerBits) != 0) // This player is interactive locally
					{
						var playerPosition = GetPlayerAudioPosition(gameState, i);
						if (playerPosition != null)
						{
							var distanceSquared = aabb.Value.DistanceSquaredTo(playerPosition.Value);
							if (distanceSquared < bestDistanceSquared)
								bestDistanceSquared = distanceSquared;
						}
					}
				}
			}

			else // Sound source is a point source
			{
				for (var i = 0; i < maxPlayers; i++) // For each possible player
				{
					if (((1 << i) & localPlayerBits) != 0) // This player is interactive locally
					{
						var playerPosition = AudioSystem.getPlayerAudioPosition(gameState, i);
						if (playerPosition != null)
						{
							var distanceSquared = Position.DistanceSquared(position, playerPosition.Value);
							if (distanceSquared < bestDistanceSquared)
								bestDistanceSquared = distanceSquared;
						}
					}
				}
			}

			return bestDistanceSquared;
		}

		private static Position? GetPlayerAudioPosition(object gameState, int i)
		{
			return AudioSystem.getPlayerAudioPosition(gameState, i);
		}

		#endregion
	}
}
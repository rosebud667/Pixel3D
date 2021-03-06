﻿// Copyright © Conatus Creative, Inc. All rights reserved.
// Licensed under the Apache 2.0 License. See LICENSE.md in the project root for license terms.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Reflection.Emit;
using Pixel3D.ActorManagement;
using Pixel3D.StateManagement;

namespace Pixel3D.Levels
{
	public class CreateLevelBehaviourCache
	{
		private static readonly char[] CommaSeparator = { ',' };

		private static readonly Dictionary<string, CreateLevelBehaviourDelegate> levelCache =
			new Dictionary<string, CreateLevelBehaviourDelegate>();

		private static readonly Dictionary<string, CreateLevelSubBehaviourDelegate> levelSubCache =
			new Dictionary<string, CreateLevelSubBehaviourDelegate>();

		private static readonly Dictionary<string, CreateLevelSubBehaviourDelegate> globalSubCache =
			new Dictionary<string, CreateLevelSubBehaviourDelegate>();

		private static readonly ReadOnlyList<ILevelSubBehaviour> NoSubBehaviours =
			new ReadOnlyList<ILevelSubBehaviour>(new List<ILevelSubBehaviour>(0));

		public static void Initialize(Assembly[] assemblies)
		{
			Type[] delegateArgumentTypes = {typeof(Level), typeof(UpdateContext)};

			// Possible constructors in priority order:
			Type[][] parameterTypeSets =
			{
				new[] {typeof(Level), typeof(UpdateContext)},
				new[] {typeof(UpdateContext)},
				new[] {typeof(Level)},
				Type.EmptyTypes
			};

			//
			// Look in all assemblies, as we may have content scattered across multiple WADs...
			//

			foreach (var assembly in assemblies)
			{
                try
                {
                    foreach (var type in assembly.GetTypes())
                    {
                        var validInterface = typeof(ILevelSubBehaviour).IsAssignableFrom(type) &&
                                             type != typeof(LevelSubBehaviour) && !type.IsAbstract;

                        if (validInterface && typeof(IGlobalLevelSubBehaviour).IsAssignableFrom(type))
                            RegisterSubBehaviourType(globalSubCache, type, delegateArgumentTypes, parameterTypeSets);
                        else if (validInterface)
                            RegisterSubBehaviourType(levelSubCache, type, delegateArgumentTypes, parameterTypeSets);

                        if (typeof(LevelBehaviour).IsAssignableFrom(type) && type != typeof(LevelBehaviour) && !type.IsAbstract)
                        {
                            foreach (var parameterTypes in parameterTypeSets)
                            {
                                var constructor = type.GetConstructor(parameterTypes);
                                if (constructor != null)
                                {
                                    // No way to convert a constructor to a delegate directly. And we want to select parameters. To IL we go!
                                    var dm = new DynamicMethod("Create_" + type.Name, typeof(LevelBehaviour), delegateArgumentTypes, type);
                                    var il = dm.GetILGenerator();

                                    // Map delegate parameters to constructor parameters
                                    foreach (var parameterType in parameterTypes)
                                        if (parameterType == typeof(Level))
                                            il.Emit(OpCodes.Ldarg_0); // <- depends on order of delegate arguments!
                                        else if (parameterType == typeof(UpdateContext))
                                            il.Emit(OpCodes.Ldarg_1); // <- depends on order of delegate arguments!
                                        else
                                            throw new InvalidOperationException(); // <- should be impossible

                                    il.Emit(OpCodes.Newobj, constructor);
                                    il.Emit(OpCodes.Ret);

                                    levelCache[type.Name] = (CreateLevelBehaviourDelegate)dm.CreateDelegate(typeof(CreateLevelBehaviourDelegate));
                                    goto foundValidConstructor;
                                }
                            }

                            Debug.WriteLine("WARNING: No valid constructor to create level behaviour: " + type);

                            foundValidConstructor:; // done
                        }
                    }
                }
                catch (Exception e)
                {
                    Trace.TraceError(e.ToString());
                }
			}
		}

		private static void RegisterSubBehaviourType(IDictionary<string, CreateLevelSubBehaviourDelegate> cache,
			Type type, Type[] delegateArgumentTypes, Type[][] parameterTypeSets)
		{
			foreach (var parameterTypes in parameterTypeSets)
			{
				var constructor = type.GetConstructor(parameterTypes);
				if (constructor != null)
				{
					var dm = new DynamicMethod("Create_" + type.Name, type, delegateArgumentTypes, type);
					var il = dm.GetILGenerator();

					// Map delegate parameters to constructor parameters
					foreach (var parameterType in parameterTypes)
						if (parameterType == typeof(Level))
							il.Emit(OpCodes.Ldarg_0); // <- depends on order of delegate arguments!
						else if (parameterType == typeof(UpdateContext))
							il.Emit(OpCodes.Ldarg_1); // <- depends on order of delegate arguments!
						else
							throw new InvalidOperationException(); // <- should be impossible

					il.Emit(OpCodes.Newobj, constructor);
					il.Emit(OpCodes.Ret);
					
					var key = type.Name.Replace("SubBehaviour", string.Empty);
					cache[key] = (CreateLevelSubBehaviourDelegate)
						dm.CreateDelegate(typeof(CreateLevelSubBehaviourDelegate));

					goto foundValidConstructor;
				}
			}

			foundValidConstructor:; // done
		}

		public static LevelBehaviour CreateLevelBehaviour(string behaviour, Level level, UpdateContext context)
		{
			if (behaviour == null)
				return new LevelBehaviour();

			CreateLevelBehaviourDelegate createMethod;
			if (levelCache.TryGetValue(behaviour, out createMethod))
			{
				var levelBehaviour = createMethod(level, context);

				InjectSubBehaviours(level, levelBehaviour, context);

				return levelBehaviour;
			}

			Debug.WriteLine("Missing LevelBehaviour called \"" + behaviour + "\"");
			return new LevelBehaviour();
		}

		private static void InjectSubBehaviours(Level level, LevelBehaviour levelBehaviour, UpdateContext context)
		{
			var hasGlobalSubBehaviours = globalSubCache != null && globalSubCache.Count > 0;

			var subBehaviourString = level.properties.GetString(Symbols.SubBehaviours);
			if (!hasGlobalSubBehaviours && subBehaviourString == null)
			{
				levelBehaviour.subBehaviours = NoSubBehaviours;
				return;
			}

			string[] subBehaviours = null;
			if (subBehaviourString != null)
			{
				subBehaviours = subBehaviourString.Split(CommaSeparator, StringSplitOptions.RemoveEmptyEntries);
				if (!hasGlobalSubBehaviours && subBehaviours.Length == 0)
				{
					levelBehaviour.subBehaviours = NoSubBehaviours;
					return;
				}
			}

			var subList = new List<ILevelSubBehaviour>();

			if (hasGlobalSubBehaviours)
				foreach (var globalSubBehaviour in globalSubCache)
					subList.Add(globalSubBehaviour.Value(level, context));

			if (subBehaviours != null)
				foreach (var subBehaviour in subBehaviours)
				{
					CreateLevelSubBehaviourDelegate createSubMethod;
					if (levelSubCache.TryGetValue(subBehaviour, out createSubMethod))
						subList.Add(createSubMethod(level, context));
				}

			levelBehaviour.subBehaviours = new ReadOnlyList<ILevelSubBehaviour>(subList);
		}

		private delegate LevelBehaviour CreateLevelBehaviourDelegate(Level level, UpdateContext context);

		private delegate ILevelSubBehaviour CreateLevelSubBehaviourDelegate(Level level, UpdateContext context);
	}
}
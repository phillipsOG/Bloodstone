using System;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Il2CppInterop.Runtime;
using Unity.Collections;
using Unity.Entities;

namespace Bloodstone.ECS
{
    public static unsafe class ECSUtil
    {
        public enum ECSVersion
        {
            UNKNOWN,
            V0_17,
            V0_51,
            V1_0
        }

        private static ECSVersion _currentECSVersion = ECSVersion.UNKNOWN;

        public static ECSVersion CurrentECSVersion
        {
            get
            {
                if (_currentECSVersion != ECSVersion.UNKNOWN)
                    return _currentECSVersion;

                var archetypeFlagsType = Assembly.GetAssembly(typeof(EntityManager)).GetType("Unity.Entities.ArchetypeFlags");
                var typeFlagsNames = Enum.GetNames(archetypeFlagsType);
                if (typeFlagsNames.Contains("HasHybridComponents"))
                {
                    _currentECSVersion = ECSVersion.V0_17;
                    return _currentECSVersion;
                }

                if (typeFlagsNames.Contains("HasWeakAssetRefs"))
                {
                    _currentECSVersion = ECSVersion.V0_51;
                    return _currentECSVersion;
                }

                BloodstonePlugin.Logger.LogWarning("Failed to determine ECS version!");
                return _currentECSVersion;
            }
        }

        public static int GetModTypeIndex<T>()
        {
            try
            {
                var index = SharedTypeIndex<T>.Ref.Data;

                if (index <= 0)
                {
                    throw new ArgumentException($"Failed to get type index for {typeof(T).FullName}");
                }

                return index;
            }
            catch (Exception e)
            {
                int index = TypeManager.GetTypeIndex(Il2CppType.Of<T>());
                if (index <= 0)
                {
                    throw new ArgumentException($"Failed to get type index for {typeof(T).FullName}");
                }

                return index;
            }
        }

        public const int ClearFlagsMask = 0x007FFFFF;

        public static ref readonly TypeManager.TypeInfo GetTypeInfo(int typeIndex)
        {
            return ref TypeManager.GetTypeInfoPointer()[typeIndex & ClearFlagsMask];
        }

        public static bool ExistsSafe(this EntityManager entityManager, Entity entity)
        {
            try
            {
                return entityManager.Exists(entity);
            }
            catch (Exception)
            {
                return false;
            }
        }

        public static ComponentType ReadOnly<T>()
        {
            int typeIndex = GetModTypeIndex<T>();
            ComponentType componentType = ComponentType.FromTypeIndex(typeIndex);
            componentType.AccessModeType = ComponentType.AccessMode.ReadOnly;
            return componentType;
        }

        public static void SetEnabled(this EntityManager entityManager, Entity entity, bool enabled)
        {
            if (IsEntityEnabled(entityManager, entity) == enabled)
            {
                return;
            }

            ComponentType componentType = ReadOnly<Disabled>();
            if (entityManager.HasModComponent<LinkedEntityGroup>(entity))
            {
                NativeArray<Entity> entities = entityManager
                    .GetModBuffer<LinkedEntityGroup>(entity)
                    .Reinterpret<Entity>()
                    .ToIl2CppNativeArray(Allocator.TempJob);
                if (enabled)
                {
                    entityManager.RemoveComponent(entities, componentType);
                }
                else
                {
                    entityManager.AddComponent(entities, componentType);
                }

                entities.Dispose();
                return;
            }

            if (!enabled)
            {
                entityManager.AddComponent(entity, componentType);
                return;
            }

            entityManager.RemoveComponent(entity, componentType);
        }

        public static string GetName(EntityManager entityManager, Entity entity)
        {
            if (CurrentECSVersion < ECSVersion.V0_51)
            {
                return entity.ToString();
            }

            return GetName_Internal(entityManager, entity);
        }

        private static string GetName_Internal(EntityManager entityManager, Entity entity)
        {
            string result = "";
            var method1 = typeof(EntityManager).GetMethod("GetName", AccessTools.all, new[] { typeof(Entity) });
            if (method1 != null)
            {
                result = (string)method1.Invoke(entityManager, new object[] { entity });
            }

            var method2 = typeof(EntityManager).GetMethod("GetName", AccessTools.all, new[] { typeof(Entity), typeof(FixedString64).MakeByRefType() });
            if (method2 != null)
            {
                object[] args = { entity, new FixedString64("") };
                method1.Invoke(entityManager, args);
                result = (string)args[1];
            }

            return string.IsNullOrEmpty(result) ? entity.ToString() : result;
        }

        public static void SetName(EntityManager entityManager, Entity entity, string name)
        {
            if (CurrentECSVersion < ECSVersion.V0_51)
            {
                return;
            }

            SetName_Internal(entityManager, entity, name);
        }

        private static void SetName_Internal(EntityManager entityManager, Entity entity, string name)
        {
            var method1 = typeof(EntityManager).GetMethod("SetName", AccessTools.all, new[] { typeof(Entity), typeof(string) });
            if (method1 != null)
            {
                method1.Invoke(entityManager, new object[] { entity, name });
                return;
            }

            var method2 = typeof(EntityManager).GetMethod("SetName", AccessTools.all, new[] { typeof(Entity), typeof(FixedString64) });
            if (method2 != null)
            {
                FixedString64 strBytes = new FixedString64(name);
                method1.Invoke(entityManager, new object[] { entity, strBytes });
            }
        }

        public static bool IsEntityEnabled(EntityManager entityManager, Entity entity)
        {
            return !entityManager.HasComponent(entity, ReadOnly<Disabled>());
        }

        /// <summary>
        /// Gets the dynamic buffer of an entity.
        /// </summary>
        /// <param name="entity">The entity.</param>
        /// <param name="isReadOnly">Specify whether the access to the component through this object is read only
        /// or read and write. </param>
        /// <typeparam name="T">The type of the buffer's elements.</typeparam>
        /// <returns>The DynamicBuffer object for accessing the buffer contents.</returns>
        /// <exception cref="ArgumentException">Thrown if T is an unsupported type.</exception>
        public static ModDynamicBuffer<T> GetModBuffer<T>(this EntityManager entityManager, Entity entity, bool isReadOnly = false) where T : unmanaged
        {
            var typeIndex = GetModTypeIndex<T>();
            var access = entityManager.GetCheckedEntityDataAccess();

            if (!access->IsInExclusiveTransaction)
            {
                if (isReadOnly)
                    access->DependencyManager->CompleteWriteDependency(typeIndex);
                else
                    access->DependencyManager->CompleteReadAndWriteDependency(typeIndex);
            }

            BufferHeader* header;
            if (isReadOnly)
            {
                header = (BufferHeader*)access->EntityComponentStore->GetComponentDataWithTypeRO(entity, typeIndex);
            }
            else
            {
                header = (BufferHeader*)access->EntityComponentStore->GetComponentDataWithTypeRW(entity, typeIndex,
                    access->EntityComponentStore->GlobalSystemVersion);
            }

            int internalCapacity = GetTypeInfo(typeIndex).BufferCapacity;
            return new ModDynamicBuffer<T>(header, internalCapacity);
        }

        public static T GetModComponentData<T>(this EntityManager entityManager, Entity entity)
        {
            int typeIndex = GetModTypeIndex<T>();
            var dataAccess = entityManager.GetCheckedEntityDataAccess();

            if (!dataAccess->HasComponent(entity, ComponentType.FromTypeIndex(typeIndex)))
            {
                throw new InvalidOperationException($"Tried to get component data for component {typeof(T).FullName}, which entity does not have!");
            }

            if (!dataAccess->IsInExclusiveTransaction)
            {
                (&dataAccess->m_DependencyManager)->CompleteWriteDependency(typeIndex);
            }

            byte* ret = dataAccess->EntityComponentStore->GetComponentDataWithTypeRO(entity, typeIndex);

            return Unsafe.Read<T>(ret);
        }

        /// <summary>
        /// Set Component Data of type.
        /// This method will work on any type, including mod created ones
        /// </summary>
        /// <param name="entity">Target Entity</param>
        /// <param name="entityManager">World EntityManager</param>
        /// <param name="component">Component Data</param>
        /// <typeparam name="T">Component Type</typeparam>
        public static void SetModComponentData<T>(this EntityManager entityManager, Entity entity, T component)
        {
            int typeIndex = GetModTypeIndex<T>();
            var dataAccess = entityManager.GetCheckedEntityDataAccess();
            var componentStore = dataAccess->EntityComponentStore;

            if (!dataAccess->HasComponent(entity, ComponentType.FromTypeIndex(typeIndex)))
            {
                throw new InvalidOperationException($"Tried to set component data for component {typeof(T).FullName}, which entity does not have!");
            }

            if (!dataAccess->IsInExclusiveTransaction)
            {
                (&dataAccess->m_DependencyManager)->CompleteReadAndWriteDependency(typeIndex);
            }

            byte* writePtr = componentStore->GetComponentDataWithTypeRW(entity, typeIndex, componentStore->m_GlobalSystemVersion);
            Unsafe.Copy(writePtr, ref component);
        }

        public static bool HasModComponent<T>(this EntityManager entityManager, Entity entity)
        {
            ComponentType componentType = ComponentType.FromTypeIndex(GetModTypeIndex<T>());
            var dataAccess = entityManager.GetCheckedEntityDataAccess();

            return dataAccess->HasComponent(entity, componentType);
        }
    }
}
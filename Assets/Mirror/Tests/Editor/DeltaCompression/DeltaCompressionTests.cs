using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

namespace Mirror.Tests
{
    // inventory is interesting. mostly ints.
    public struct InventorySlot
    {
        public int itemId;
        public int amount;
    }

    // skills are interesting. ushorts, doubles, etc. are all != 4 byte ints.
    public struct SkillSlot
    {
        public ushort skillId;
        public double cooldown;
    }

    // test monster for compression.
    // can't be in separate file because Unity complains about it being an Editor
    // script because it's in the Editor folder.
    public class CompressionMonster : NetworkBehaviour
    {
        // a variable length name field
        [SyncVar] public string monsterName;

        // a couple of fixed length fields
        [SyncVar] public int health;
        [SyncVar] public int mana;
        // something != 4 byte inbetween
        [SyncVar] public byte level;
        [SyncVar] public Vector3 position;
        [SyncVar] public Quaternion rotation;

        // variable length inventory
        SyncList<InventorySlot> inventory = new SyncList<InventorySlot>();

        // a couple more fixed fields AFTER variable length inventory
        // to make sure they are still delta compressed decently.
        [SyncVar] public int strength;
        [SyncVar] public int intelligence;
        [SyncVar] public int damage;
        [SyncVar] public int defense;

        // variable length skills
        SyncList<SkillSlot> skills = new SyncList<SkillSlot>();

        public void Initialize(
            string monsterName,
            int health, int mana,
            byte level,
            Vector3 position, Quaternion rotation,
            List<InventorySlot> inventory,
            int strength, int intelligence,
            int damage, int defense,
            List<SkillSlot> skills)
        {
            this.monsterName = monsterName;
            this.health = health;
            this.mana = mana;
            this.level = level;
            this.position = position;
            this.rotation = rotation;

            foreach (InventorySlot slot in inventory)
                this.inventory.Add(slot);

            this.strength = strength;
            this.intelligence = intelligence;
            this.damage = damage;
            this.defense = defense;

            foreach (SkillSlot slot in skills)
                this.skills.Add(slot);
        }
    }

    // all compression approaches should inherit to compare them
    public abstract class DeltaCompressionTests
    {
        // two snapshots
        protected CompressionMonster A;
        protected CompressionMonster B;

        // the algorithm to use
        public abstract void ComputeDelta(NetworkWriter from, NetworkWriter to, NetworkWriter result);

        [SetUp]
        public void SetUp()
        {
            // create the monster with unique values
            A = new GameObject().AddComponent<CompressionMonster>();
            A.Initialize(
                // name, health, mana, level
                "Skeleton",
                100,
                200,
                60,
                // position, rotation
                new Vector3(10, 20, 30),
                Quaternion.identity,
                // inventory
                new List<InventorySlot>{
                    new InventorySlot{amount=0, itemId=0},
                    new InventorySlot{amount=1, itemId=42},
                    new InventorySlot{amount=50, itemId=43},
                    new InventorySlot{amount=0, itemId=0}
                },
                // strength, intelligence, damage, defense
                10,
                11,
                1000,
                500,
                // skills
                new List<SkillSlot>{
                    new SkillSlot{skillId=4, cooldown=0},
                    new SkillSlot{skillId=8, cooldown=1},
                    new SkillSlot{skillId=16, cooldown=2.5},
                    new SkillSlot{skillId=23, cooldown=60}
                }
            );

            // change it a little for second snapshot
            B = new GameObject().AddComponent<CompressionMonster>();
            B.Initialize(
                // name, health, mana, level
                "Skeleton (Dead)",
                0,
                99,
                61,
                // position, rotation
                new Vector3(11, 22, 30),
                Quaternion.identity,
                // inventory
                new List<InventorySlot>{
                    new InventorySlot{amount=5, itemId=42},
                    new InventorySlot{amount=6, itemId=43},
                },
                // strength, intelligence, damage, defense
                12,
                13,
                5000,
                2000,
                // skills: assume two were buffs and are now gone
                new List<SkillSlot>{
                    new SkillSlot{skillId=16, cooldown=0},
                    new SkillSlot{skillId=23, cooldown=25}
                }
            );
        }

        // quick test to write the uncompressed component.
        // to make sure mirror generates serialization etc.
        [Test]
        public void Uncompressed()
        {
            NetworkWriter writerA = new NetworkWriter();
            A.OnSerialize(writerA, true);
            Debug.Log($"A uncompressed size: {writerA.Position} bytes");

            NetworkWriter writerB = new NetworkWriter();
            B.OnSerialize(writerB, true);
            Debug.Log($"B uncompressed size: {writerB.Position} bytes");
        }

        // run the delta encoding
        [Test]
        public void Delta()
        {
            // serialize both
            NetworkWriter writerA = new NetworkWriter();
            A.OnSerialize(writerA, true);

            NetworkWriter writerB = new NetworkWriter();
            B.OnSerialize(writerB, true);

            // compute delta
            NetworkWriter result = new NetworkWriter();
            ComputeDelta(writerA, writerB, result);
            Debug.Log($"A={writerA.Position} bytes\nB={writerB.Position} bytes\n=>Delta={result.Position}bytes");
        }

        // measure performance. needs to be fast enough.
        [Test]
        public void Benchmark()
        {
            // serialize both
            NetworkWriter writerA = new NetworkWriter();
            A.OnSerialize(writerA, true);

            NetworkWriter writerB = new NetworkWriter();
            B.OnSerialize(writerB, true);

            // compute delta several times (assume 100k entities in the world)
            NetworkWriter result = new NetworkWriter();
            for (int i = 0; i < 100000; ++i)
            {
                // reset write each time. don't want to measure resizing.
                result.Position = 0;
                ComputeDelta(writerA, writerB, result);
            }
        }
    }
}

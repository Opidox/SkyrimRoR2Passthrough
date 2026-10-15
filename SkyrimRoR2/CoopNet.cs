using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using BepInEx.Logging;
using SkyrimRoR2.Shared;
using UnityEngine;
using UnityEngine.Networking;

namespace SkyrimRoR2
{
    // 0.70 : la coop, partie "tuyaux".
    //
    //  - CoopMessage : un message réseau de RoR2 (UNet) qui transporte nos octets. Un seul numéro de message pour tout
    //    (CoopNet.MsgType), le premier octet dit de quoi il s'agit (CoopKind).
    //  - Packet : écrire / lire ces octets.
    //  - CoopZone : le pont "SkyrimRoR2_Coop" avec NOTRE Skyrim (voir skse/src/Coop.cpp).
    internal sealed class CoopMessage : MessageBase
    {
        public byte[] Data = new byte[0];

        public CoopMessage() { }
        public CoopMessage(byte[] data) { Data = data ?? new byte[0]; }

        public override void Serialize(NetworkWriter writer)
        {
            writer.WriteBytesAndSize(Data, Data.Length);
        }

        public override void Deserialize(NetworkReader reader)
        {
            Data = reader.ReadBytesAndSize() ?? new byte[0];
        }
    }

    internal enum CoopKind : byte
    {
        // invité -> hôte
        Report = 1,        // son Skyrim : lieu, pieds, niveau, vie en plus, charge, ennemis proches (en morceaux)
        Claim = 2,         // calage demandé pour un nouveau lieu
        Hit = 3,           // un coup reçu dans son Skyrim
        Loot = 4,          // un objet RoR2 gagné dans son Skyrim (rang, nombre)
        Inventory = 5,     // ses objets portés dans Skyrim (texte de inventaire_charge.txt, en morceaux)
        Respawn = 6,       // réveil dans son Skyrim après une mort
        Survivor = 7,      // changement de survivant
        Opened = 8,        // contenant ouvert dans son Skyrim
        Finished = 9,      // coffre du boss terminé dans son Skyrim
        Loadout = 10,      // skin ou arme (le changement demande une réapparition, faite par l'hôte)
        // hôte -> invités
        Places = 20,       // la table des lieux et de leur calage
        Ghosts = 21,       // places voulues des ennemis dans son Skyrim (monde RoR2)
        Damage = 22,       // dégâts à appliquer à un ennemi de son Skyrim
        Pickup = 23,       // objet donné (pour l'annonce dans Skyrim)
        State = 24,        // son état : à terre, relèvement, difficulté commune, charge d'un coéquipier...
        Text = 25,         // message à afficher dans Skyrim
        RemoteOpened = 26, // contenant ouvert par un coéquipier
        RemoteFinished = 27, // coffre du boss terminé par un coéquipier
        InventoryDone = 28,  // objets portés appliqués (numéro de la demande)
    }

    internal static class Packet
    {
        public static BinaryWriter Begin(CoopKind kind, out MemoryStream stream)
        {
            stream = new MemoryStream(256);
            var writer = new BinaryWriter(stream, Encoding.UTF8);
            writer.Write((byte)kind);
            return writer;
        }

        public static void Vector(BinaryWriter w, Vector3 v) { w.Write(v.x); w.Write(v.y); w.Write(v.z); }
        public static Vector3 Vector(BinaryReader r) => new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());

        public static void Text(BinaryWriter w, string text)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(text ?? "");
            int length = Math.Min(bytes.Length, 1000);
            w.Write((ushort)length);
            w.Write(bytes, 0, length);
        }

        public static string Text(BinaryReader r)
        {
            int length = r.ReadUInt16();
            return Encoding.UTF8.GetString(r.ReadBytes(length));
        }

        public static void Actor(BinaryWriter w, ActorEntry e)
        {
            w.Write(e.FormId); w.Write(e.Flags);
            w.Write(e.RelX); w.Write(e.RelY); w.Write(e.RelZ);
            w.Write(e.Height); w.Write(e.Health); w.Write(e.MaxHealth);
            w.Write(e.Width); w.Write(e.Length); w.Write(e.Yaw);
        }

        public static ActorEntry Actor(BinaryReader r)
        {
            return new ActorEntry
            {
                FormId = r.ReadUInt32(), Flags = r.ReadUInt32(),
                RelX = r.ReadSingle(), RelY = r.ReadSingle(), RelZ = r.ReadSingle(),
                Height = r.ReadSingle(), Health = r.ReadSingle(), MaxHealth = r.ReadSingle(),
                Width = r.ReadSingle(), Length = r.ReadSingle(), Yaw = r.ReadSingle(),
            };
        }
    }

    // Le pont avec notre Skyrim pour la coop (même disposition que CoopFromRoR2 / CoopFromSkyrim de bridge_protocol.h).
    internal sealed class CoopZone
    {
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr CreateFileMapping(IntPtr hFile, IntPtr attributes, uint protect, uint sizeHigh, uint sizeLow, string name);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr MapViewOfFile(IntPtr mapping, uint access, uint offsetHigh, uint offsetLow, UIntPtr bytes);

        private readonly ManualLogSource _log;
        private IntPtr _view;
        private bool _openTried;
        private uint _sequence;
        private uint _textSequence;
        private uint _openedWrite, _finishedWrite;
        private uint _skyOpenedRead, _skyFinishedRead;
        private bool _skyStarted;

        public CoopZone(ManualLogSource log) { _log = log; }

        private bool Open()
        {
            if (_view != IntPtr.Zero) return true;
            if (_openTried) return false;
            _openTried = true;
            IntPtr mapping = CreateFileMapping(new IntPtr(-1), IntPtr.Zero, 0x04, 0, (uint)BridgeInfo.CoopMapSize, BridgeInfo.CoopMapName);
            if (mapping != IntPtr.Zero) _view = MapViewOfFile(mapping, 0xF001F, 0, 0, (UIntPtr)BridgeInfo.CoopMapSize);
            if (_view == IntPtr.Zero) { _log.LogWarning("Coop : pont avec Skyrim impossible a ouvrir"); return false; }
            // Les compteurs déjà là (RoR2 relancé) : on continue à partir d'eux.
            _textSequence = (uint)Marshal.ReadInt32(_view, BridgeInfo.CoopTextSequence);
            _openedWrite = (uint)Marshal.ReadInt32(_view, BridgeInfo.CoopOpenedWrite);
            _finishedWrite = (uint)Marshal.ReadInt32(_view, BridgeInfo.CoopFinishedWrite);
            return true;
        }

        private static void Float(IntPtr at, int offset, float value) =>
            Marshal.WriteInt32(at, offset, BitConverter.ToInt32(BitConverter.GetBytes(value), 0));

        private static void Chars(IntPtr at, int offset, int size, string text)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(text ?? "");
            int length = Math.Min(bytes.Length, size - 1);
            while (length > 0 && length < bytes.Length && (bytes[length] & 0xC0) == 0x80) length--;  // pas de lettre coupée
            for (int i = 0; i < size; i++) Marshal.WriteByte(at, offset + i, i < length ? bytes[i] : (byte)0);
        }

        // L'état de la partie coop pour notre Skyrim (à chaque image).
        public void Write(bool active, int players, float difficulty, bool keepDifficulty, bool downed, float revive, string reviveLabel,
            uint chargeChest, float chargeProgress)
        {
            if (!Open()) return;
            Marshal.WriteInt32(_view, BridgeInfo.CoopActive, active ? 1 : 0);
            Marshal.WriteInt32(_view, BridgeInfo.CoopPlayers, players);
            Float(_view, BridgeInfo.CoopDifficulty, difficulty);
            Marshal.WriteInt32(_view, BridgeInfo.CoopKeepDifficulty, keepDifficulty ? 1 : 0);
            Marshal.WriteInt32(_view, BridgeInfo.CoopDowned, downed ? 1 : 0);
            Float(_view, BridgeInfo.CoopRevive, revive);
            Chars(_view, BridgeInfo.CoopReviveLabel, 64, reviveLabel);
            Marshal.WriteInt32(_view, BridgeInfo.CoopChargeChest, unchecked((int)chargeChest));
            Float(_view, BridgeInfo.CoopChargeProgress, chargeProgress);
            Marshal.WriteInt32(_view, 4, unchecked((int)++_sequence));
            Marshal.WriteInt32(_view, 0, unchecked((int)BridgeInfo.CoopMagic));
        }

        public void ShowText(string text)
        {
            if (string.IsNullOrEmpty(text) || !Open()) return;
            Chars(_view, BridgeInfo.CoopText, BridgeInfo.CoopTextSize, text);
            System.Threading.Thread.MemoryBarrier();
            Marshal.WriteInt32(_view, BridgeInfo.CoopTextSequence, unchecked((int)++_textSequence));
        }

        public void PushOpened(uint formId)
        {
            if (!Open()) return;
            Marshal.WriteInt32(_view, BridgeInfo.CoopOpened + (int)(_openedWrite % BridgeInfo.CoopRing) * 4, unchecked((int)formId));
            System.Threading.Thread.MemoryBarrier();
            Marshal.WriteInt32(_view, BridgeInfo.CoopOpenedWrite, unchecked((int)++_openedWrite));
        }

        public void PushFinished(uint formId)
        {
            if (!Open()) return;
            Marshal.WriteInt32(_view, BridgeInfo.CoopFinished + (int)(_finishedWrite % 16) * 4, unchecked((int)formId));
            System.Threading.Thread.MemoryBarrier();
            Marshal.WriteInt32(_view, BridgeInfo.CoopFinishedWrite, unchecked((int)++_finishedWrite));
        }

        // Ce que notre Skyrim a fait depuis la dernière fois : contenants ouverts, coffres de boss terminés, et sa charge.
        public void ReadSkyrim(List<uint> opened, List<uint> finished, out uint chargeChest, out float chargeProgress)
        {
            opened.Clear();
            finished.Clear();
            chargeChest = 0;
            chargeProgress = 0f;
            if (!Open()) return;
            IntPtr sky = IntPtr.Add(_view, BridgeInfo.CoopSkyrimOffset);
            if ((uint)Marshal.ReadInt32(sky, 0) != BridgeInfo.CoopSkyMagic) return;
            uint openedWritten = (uint)Marshal.ReadInt32(sky, BridgeInfo.CoopSkyOpenedWrite);
            uint finishedWritten = (uint)Marshal.ReadInt32(sky, BridgeInfo.CoopSkyFinishedWrite);
            System.Threading.Thread.MemoryBarrier();
            if (!_skyStarted)
            {
                _skyStarted = true;
                _skyOpenedRead = openedWritten;
                _skyFinishedRead = finishedWritten;
            }
            if (openedWritten - _skyOpenedRead > BridgeInfo.CoopRing) _skyOpenedRead = openedWritten - (uint)BridgeInfo.CoopRing;
            while (_skyOpenedRead != openedWritten)
            {
                opened.Add((uint)Marshal.ReadInt32(sky, BridgeInfo.CoopSkyOpened + (int)(_skyOpenedRead % BridgeInfo.CoopRing) * 4));
                _skyOpenedRead++;
            }
            if (finishedWritten - _skyFinishedRead > 16) _skyFinishedRead = finishedWritten - 16;
            while (_skyFinishedRead != finishedWritten)
            {
                finished.Add((uint)Marshal.ReadInt32(sky, BridgeInfo.CoopSkyFinished + (int)(_skyFinishedRead % 16) * 4));
                _skyFinishedRead++;
            }
            chargeChest = (uint)Marshal.ReadInt32(sky, BridgeInfo.CoopSkyChargeChest);
            chargeProgress = BitConverter.ToSingle(BitConverter.GetBytes(Marshal.ReadInt32(sky, BridgeInfo.CoopSkyChargeProgress)), 0);
            if (float.IsNaN(chargeProgress)) chargeProgress = 0f;
        }
    }
}

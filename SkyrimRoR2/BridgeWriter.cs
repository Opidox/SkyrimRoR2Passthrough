using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using SkyrimRoR2.Shared;

namespace SkyrimRoR2
{
    // Crée (ou ouvre) le "tableau blanc" partagé dans Windows, pour y écrire l'état
    // du survivant et y lire les commandes envoyées par Skyrim.
    // On parle directement à Windows (kernel32.dll), comme le fait le côté Skyrim.
    internal sealed class BridgeWriter : IDisposable
    {
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr CreateFileMapping(IntPtr hFile, IntPtr attributes, uint protect,
            uint maximumSizeHigh, uint maximumSizeLow, string name);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr MapViewOfFile(IntPtr hFileMappingObject, uint desiredAccess,
            uint fileOffsetHigh, uint fileOffsetLow, UIntPtr numberOfBytesToMap);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool UnmapViewOfFile(IntPtr baseAddress);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr handle);

        private const uint PAGE_READWRITE = 0x04;
        private const uint FILE_MAP_ALL_ACCESS = 0xF001F;
        private static readonly IntPtr INVALID_HANDLE_VALUE = new IntPtr(-1);

        private IntPtr _handle;
        private IntPtr _view;

        public bool IsOpen => _view != IntPtr.Zero;
        public int LastError { get; private set; }

        public BridgeWriter()
        {
            // Crée le tableau blanc, ou ouvre celui que Skyrim a déjà créé.
            _handle = CreateFileMapping(INVALID_HANDLE_VALUE, IntPtr.Zero, PAGE_READWRITE,
                0, (uint)BridgeInfo.MapSize, BridgeInfo.MapName);
            if (_handle == IntPtr.Zero)
            {
                LastError = Marshal.GetLastWin32Error();
                return;
            }

            _view = MapViewOfFile(_handle, FILE_MAP_ALL_ACCESS, 0, 0, (UIntPtr)BridgeInfo.MapSize);
            if (_view == IntPtr.Zero)
            {
                LastError = Marshal.GetLastWin32Error();
            }
        }

        // Adresse d'une zone du pont (pour écrire champ par champ), IntPtr.Zero si le pont est fermé.
        public IntPtr ZonePointer(int offset) => IsOpen ? IntPtr.Add(_view, offset) : IntPtr.Zero;

        // Zone 1 : recopie l'état du survivant au début du tableau blanc.
        public void Write(BridgeData data)
        {
            if (!IsOpen) return;
            Marshal.StructureToPtr(data, _view, false);
        }

        // Zone 2 : lit les commandes envoyées par Skyrim. Renvoie false si la zone est vide.
        public bool ReadInput(out SkyrimInput input)
        {
            input = default;
            if (!IsOpen) return false;
            IntPtr where = IntPtr.Add(_view, BridgeInfo.InputOffset);
            input = (SkyrimInput)Marshal.PtrToStructure(where, typeof(SkyrimInput));
            return input.Magic == BridgeInfo.InputMagicValue;
        }

        // Zone 3 : lit la liste des ennemis Skyrim proches. Renvoie false si la zone est vide.
        public bool ReadActors(List<ActorEntry> into, out uint sequence)
        {
            into.Clear();
            sequence = 0;
            if (!IsOpen) return false;

            IntPtr table = IntPtr.Add(_view, BridgeInfo.ActorOffset);
            if ((uint)Marshal.ReadInt32(table, 0) != BridgeInfo.ActorMagicValue) return false;
            sequence = (uint)Marshal.ReadInt32(table, 4);
            int count = Math.Min(Marshal.ReadInt32(table, 8), BridgeInfo.MaxActors);

            for (int i = 0; i < count; i++)
            {
                IntPtr entry = IntPtr.Add(table, BridgeInfo.ActorHeaderSize + i * BridgeInfo.ActorEntrySize);
                into.Add((ActorEntry)Marshal.PtrToStructure(entry, typeof(ActorEntry)));
            }
            return true;
        }

        // Zone 4 : ajoute un dégât dans la file, pour que Skyrim l'applique.
        // attackSlot : case de l'attaque (0 M1, 1 M2, 2 Maj, 3 R, 4 alliés, 5 objet/équipement, -1 inconnue) ;
        // attackVariant : sa variante ; skillCode : compétence d'un objet ou équipement (voir SkillSource, 0 : aucune).
        // formId 0 : pas de dégât, seulement de l'expérience (amount) pour skillCode (utilisation d'un équipement).
        // remote (0.70, coop) : coup d'un coéquipier : Skyrim applique les dégâts sans expérience ni atouts du joueur.
        public void WriteDamage(uint formId, float amount, bool crit, int attackSlot = -1, int attackVariant = 0, int skillCode = 0, bool remote = false)
        {
            if (!IsOpen) return;
            IntPtr ring = IntPtr.Add(_view, BridgeInfo.DamageOffset);

            // On écrit la signature au premier dégât (Skyrim ignore la file tant qu'elle manque).
            if ((uint)Marshal.ReadInt32(ring, 0) != BridgeInfo.DamageMagicValue)
                Marshal.WriteInt32(ring, 0, unchecked((int)BridgeInfo.DamageMagicValue));

            uint writeCount = (uint)Marshal.ReadInt32(ring, 4);
            int slot = (int)(writeCount % BridgeInfo.DamageSlots);
            var damage = new DamageEvent
            {
                FormId = formId, Amount = amount, Flags = (crit ? BridgeInfo.DamageCrit : 0u) | (remote ? BridgeInfo.DamageRemote : 0u),
                Reserved = (uint)((attackSlot + 1) & 0xFF) | (uint)((Math.Max(0, attackVariant) & 0xFF) << 8) |
                           (uint)((Math.Max(0, skillCode) & 0xFF) << 16),
            };
            Marshal.StructureToPtr(damage, IntPtr.Add(ring, BridgeInfo.DamageHeaderSize + slot * BridgeInfo.DamageEventSize), false);

            // D'abord l'événement, ENSUITE le compteur : Skyrim ne lit jamais un dégât à moitié écrit.
            Thread.MemoryBarrier();
            Marshal.WriteInt32(ring, 4, unchecked((int)(writeCount + 1)));
        }

        // Zone 5 : récupère les coups reçus par le joueur dans Skyrim depuis le dernier appel.
        private uint _hitsRead;
        private bool _hitsStarted;

        public void ReadNewHits(List<HitEvent> into)
        {
            into.Clear();
            if (!IsOpen) return;
            IntPtr ring = IntPtr.Add(_view, BridgeInfo.HitOffset);
            if ((uint)Marshal.ReadInt32(ring, 0) != BridgeInfo.HitMagicValue) return;

            uint written = (uint)Marshal.ReadInt32(ring, 4);
            Thread.MemoryBarrier();

            if (!_hitsStarted)
            {
                _hitsRead = written;  // on ignore les coups d'avant notre arrivée
                _hitsStarted = true;
                return;
            }
            if (written - _hitsRead > BridgeInfo.HitSlots)
                _hitsRead = written - (uint)BridgeInfo.HitSlots;  // trop de retard : on garde les plus récents

            while (_hitsRead != written)
            {
                int slot = (int)(_hitsRead % BridgeInfo.HitSlots);
                IntPtr entry = IntPtr.Add(ring, BridgeInfo.HitHeaderSize + slot * BridgeInfo.HitEventSize);
                into.Add((HitEvent)Marshal.PtrToStructure(entry, typeof(HitEvent)));
                _hitsRead++;
            }
        }

        // Zone 6 : lit les ordres de Skyrim (partie automatique, survivant). false si la zone est vide.
        public bool ReadCommand(out SkyrimCommand command)
        {
            command = default;
            if (!IsOpen) return false;
            IntPtr where = IntPtr.Add(_view, BridgeInfo.CommandOffset);
            if ((uint)Marshal.ReadInt32(where, 0) != BridgeInfo.CommandMagicValue) return false;
            command = (SkyrimCommand)Marshal.PtrToStructure(where, typeof(SkyrimCommand));
            return true;
        }

        public void Dispose()
        {
            if (_view != IntPtr.Zero) UnmapViewOfFile(_view);
            if (_handle != IntPtr.Zero) CloseHandle(_handle);
            _view = IntPtr.Zero;
            _handle = IntPtr.Zero;
        }
    }
}

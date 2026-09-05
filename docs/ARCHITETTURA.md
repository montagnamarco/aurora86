# Aurora386 — come funziona un PC, e come lo emuliamo

Questo documento spiega due cose in parallelo: **com'è fatto un PC IBM
compatibile** con processore 386, e **come Aurora386 lo riproduce in C#**.
I due piani sono tenuti vicini di proposito, perché quasi ogni scelta
dell'emulatore è la conseguenza di un dettaglio dell'hardware vero.

---

## 1. Il PC come macchina fisica

Un PC del 1990 è un processore attaccato a due bus: uno per la memoria,
uno per i dispositivi. Tutto il resto discende da qui.

```
                         ┌───────────────┐
                         │   386  CPU    │
                         └───┬───────┬───┘
                bus memoria  │       │  bus I/O
             ┌───────────────┘       └────────────────┐
             │                                        │
     ┌───────┴────────┐                    ┌──────────┴──────────┐
     │   RAM  +  ROM  │                    │     dispositivi      │
     │                │                    │                      │
     │  0x00000 RAM   │                    │  0x20  PIC  (IRQ)    │
     │  0xA0000 video │◄── stessa memoria  │  0x40  PIT  (tempo)  │
     │  0xC0000 ROM   │    del dispositivo │  0x60  8042 (tastiera)│
     │  0xF0000 BIOS  │                    │  0x70  CMOS (orologio)│
     └────────────────┘                    │  0x1F0 IDE  (dischi) │
                                           │  0x3C0 VGA  (video)  │
                                           └──────────────────────┘
```

Due modi diversi di parlare con l'hardware:

- **Memoria mappata** — il dispositivo *è* un pezzo di indirizzi. La scheda
  video occupa `0xA0000–0xBFFFF`: scrivere lì significa disegnare.
- **Porte di I/O** — uno spazio separato di 65.536 porte, raggiunto solo dalle
  istruzioni `IN` e `OUT`. La tastiera vive alla porta `0x60`.

Il 386 ha quindi **due spazi di indirizzamento distinti**: l'indirizzo `0x60`
in memoria e la porta `0x60` non hanno niente in comune.

---

## 2. La mappa della memoria reale

Il limite storico del megabyte non è arbitrario: l'8086 aveva 20 linee di
indirizzo, e ogni PC successivo ha dovuto restare compatibile con quella
disposizione.

```
0x00000  ┌──────────────────────────────┐
         │ tabella dei vettori di       │  1 KB: 256 puntatori, 4 byte l'uno.
         │ interrupt (256 × 4 byte)     │  INT 21h legge l'indirizzo a 0x84.
0x00400  ├──────────────────────────────┤
         │ BIOS Data Area               │  256 byte di stato condiviso fra
         │                              │  BIOS e sistema operativo.
0x00500  ├──────────────────────────────┤
         │                              │
         │   memoria convenzionale      │  I famosi 640 KB. Qui stanno DOS,
         │   (DOS, driver, programmi)   │  i driver e il programma in esecuzione.
         │                              │
0xA0000  ├──────────────────────────────┤
         │ memoria video grafica        │  La scheda VGA, vista come memoria.
0xB8000  ├──────────────────────────────┤
         │ memoria video testo          │  80×25 caratteri, 2 byte ciascuno:
         │                              │  il carattere e il suo colore.
0xC0000  ├──────────────────────────────┤
         │ ROM di espansione            │  BIOS video, controller SCSI, rete.
0xF0000  ├──────────────────────────────┤
         │ BIOS di sistema              │  64 KB. All'accensione il processore
         │                              │  parte da qui.
0x100000 ├──────────────────────────────┤
         │ memoria estesa (XMS)         │  Oltre il megabyte: raggiungibile
         │                              │  solo in modo protetto o unreal.
         └──────────────────────────────┘
```

### Perché la memoria video è "solo" quella zona

Non c'è una vera scheda: c'è un intervallo di indirizzi che il chipset devia
verso il dispositivo invece che verso la RAM. In Aurora386 lo stesso
meccanismo è in `PhysicalMemoryBus`, che tiene per ogni pagina di 4 KB o un
puntatore a RAM vera, o un dispositivo da chiamare:

```
scrittura a 0xB8000
        │
        ▼
┌───────────────────┐   pagina 0xB8  ┌──────────────┐
│ PhysicalMemoryBus │───────────────►│  VgaMemory   │──► segna la riga
└───────────────────┘   è di un      └──────────────┘    come "da ridisegnare"
        │               dispositivo
        │ pagina 0x01
        ▼               è RAM vera
   byte[] in memoria
```

---

## 3. Il processore: quattro modi di funzionare

Il 386 è tre processori in uno, per ragioni di compatibilità.

```
       ┌──────────────────────────────────────────────────┐
       │  MODO REALE                                      │
       │  indirizzo = segmento × 16 + offset              │
       │  1 MB raggiungibile, nessuna protezione          │
       │  È come si accende ogni PC, ancora oggi          │
       └────────────────────┬─────────────────────────────┘
                            │  imposta il bit PE di CR0
                            ▼
       ┌──────────────────────────────────────────────────┐
       │  MODO PROTETTO                                   │
       │  il segmento è un indice in una tabella di       │
       │  descrittori: base, limite, privilegi            │
       │  4 GB, memoria protetta, quattro livelli (ring)  │
       └────────┬──────────────────────────┬──────────────┘
                │ bit VM nei flag          │ bit PG di CR0
                ▼                          ▼
       ┌─────────────────┐        ┌──────────────────────┐
       │  MODO VIRTUALE  │        │  PAGINAZIONE         │
       │  8086           │        │  indirizzo virtuale  │
       │  un DOS dentro  │        │  → tabelle → fisico  │
       │  una scatola    │        │  pagine da 4 KB      │
       └─────────────────┘        └──────────────────────┘
```

E un quarto modo che non è nei manuali Intel:

> **Unreal mode.** Si entra in modo protetto, si carica un segmento con
> limite 4 GB, si torna in modo reale. Il limite **resta** nel registro
> nascosto, e da modo reale si indirizzano 4 GB. Non è documentato, è un
> effetto collaterale del silicio — e `HIMEM.SYS` ci si appoggia.
> Aurora386 lo riproduce: vedi `docs`, o i test in `CpuRealModeTests`.

### Come lo emula Aurora386

Un **interprete**, non un compilatore JIT: legge un'istruzione, la esegue,
passa alla successiva. Più lento di un traduttore, ma trasparente da
verificare — e per una macchina del 1990 la velocità basta.

```
src/Aurora386.Core/Cpu/
├── Cpu.cs             stato, registri, ciclo principale
├── Cpu.Execute.cs     opcode a un byte (il grosso della tabella)
├── Cpu.TwoByte.cs     opcode 0F xx: modo protetto, bit, LAR/LSL
├── Cpu.ModRm.cs       decodifica degli operandi e degli indirizzi
├── Cpu.Protected.cs   descrittori, gate, cambi di privilegio
├── Cpu.Paging.cs      tabelle delle pagine e TLB
├── Cpu.Strings.cs     MOVS, STOS, SCAS e i prefissi di ripetizione
├── Cpu.Shifts.cs      rotazioni e scorrimenti
├── Cpu.Bcd.cs         aritmetica decimale (AAA, DAA…)
└── LazyFlags.cs       i flag calcolati solo quando servono
```

**I flag pigri** meritano una nota. Ogni operazione aritmetica dovrebbe
aggiornare sei flag, ma quasi nessuno viene mai letto. `LazyFlags` conserva
gli operandi e calcola il flag solo se qualcuno lo chiede: è la singola
ottimizzazione che pesa di più in un interprete.

---

## 4. Gli interrupt: come l'hardware chiede attenzione

Un tasto premuto non può essere "notato" dal processore: è il dispositivo a
interrompere il programma in corso.

```
  tastiera          8259 PIC              CPU
     │                  │                  │
     │  tasto premuto   │                  │
     ├─────────────────►│                  │
     │                  │   IRQ 1          │
     │                  ├─────────────────►│  finisce l'istruzione
     │                  │                  │  salva CS:IP e i flag
     │                  │◄─────────────────┤  chiede: quale vettore?
     │                  │   vettore 09h    │
     │                  ├─────────────────►│  salta a [0x09 × 4]
     │                  │                  │
     │                  │                  ▼
     │                  │           gestore di tastiera
     │                  │           legge la porta 0x60
     │                  │           mette il tasto nel buffer
     │                  │◄──────────  OUT 0x20, 0x20  (fine interrupt)
     │                  │                  │
     │                  │                  ▼  IRET: riprende il programma
```

Due PIC in cascata, perché uno solo gestiva otto linee e non bastavano:

```
   PIC principale (0x20)         PIC secondario (0xA0)
   ├── IRQ 0  temporizzatore     ├── IRQ  8  orologio
   ├── IRQ 1  tastiera           ├── IRQ  9
   ├── IRQ 2  ─────────────────► ├── IRQ 10
   ├── IRQ 3  seriale 2          ├── IRQ 11
   ├── IRQ 4  seriale 1 (mouse)  ├── IRQ 12  mouse PS/2
   ├── IRQ 5                     ├── IRQ 13
   ├── IRQ 6  floppy             ├── IRQ 14  disco IDE
   └── IRQ 7  stampante          └── IRQ 15
```

L'IRQ 2 non esiste come linea utile: è il cavo su cui si aggancia il secondo
PIC. Un dispositivo che dichiara "IRQ 2" in realtà usa l'IRQ 9.

---

## 5. Il BIOS: la scelta architetturale principale

Un emulatore può caricare una ROM originale, oppure scrivere il proprio BIOS.
Aurora386 fa la seconda, e in un modo particolare.

Nella ROM emulata ci sono **sequenze di tre byte**:

```
   0F FF 13     ← non è un'istruzione 386: è un aggancio
   CF           ← IRET
```

Quando la CPU incontra `0F FF`, chiama una funzione C#. Il byte seguente dice
quale. Il BIOS è dunque scritto in C#, ma **il guest lo raggiunge attraverso i
normali vettori di interrupt**, senza accorgersi di nulla.

```
   programma DOS
        │  INT 13h  (leggi un settore)
        ▼
   vettore 0x13 ──► F000:1030 nella ROM
                         │
                         │  0F FF 13
                         ▼
                  ┌──────────────┐
                  │ C#: DiskServices.Handle() │  legge dall'immagine su disco
                  └──────────────┘             │  e scrive nella memoria del guest
                         │
                         ▼  CF (IRET)
                    torna al programma
```

Il vantaggio è poter mettere un punto di osservazione ovunque; il costo è
dover implementare ogni servizio che il guest si aspetti di trovare.

### Dove sta cosa

```
src/Aurora386.Core/Bios/
├── BiosRom.cs           costruisce la ROM e i punti di aggancio
├── BiosDataArea.cs      i 256 byte di stato a 0x400
├── DiskServices.cs      INT 13h — dischi
├── VideoServices.cs     INT 10h — video
├── KeyboardServices.cs  INT 16h — tastiera
├── MouseServices.cs     INT 33h — mouse
├── XmsManager.cs        memoria estesa (quello che offre HIMEM.SYS)
├── FpuEmulator.cs       coprocessore software, sul vettore 7
└── Extended80.cs        il formato a 80 bit dell'80387
```

---

## 6. Il coprocessore che non c'è

Questa macchina non ha un 80387, ed è una situazione storicamente comune: i
386SX si vendevano senza. Il meccanismo previsto da Intel è elegante.

```
   il programma esegue:  FADD  qword [bx]
        │
        ▼
   la CPU guarda CR0.EM ──► è 1: nessun coprocessore
        │
        ▼
   eccezione 7 "dispositivo non disponibile"
        │
        ▼
   vettore 7 ──► FpuEmulator.Handle()
                      │
                      │  1. legge CS:IP dallo stack
                      │  2. rilegge l'istruzione dalla memoria del guest
                      │  3. la decodifica di nuovo (prefissi, ModRM, indirizzo)
                      │  4. esegue il calcolo
                      │  5. AVANZA IP oltre l'istruzione   ← senza questo,
                      ▼                                       ciclo infinito
                 torna al programma
```

Il punto 5 è quello che rende il meccanismo delicato: il fault riporta
l'indirizzo dell'istruzione, non quello successivo. Un gestore che si limita a
tornare rimette la CPU sulla stessa istruzione, che rifà fault, per sempre.

I registri sono uno **stack di otto**, con un puntatore alla cima che ruota:

```
   FLD 2.5           FLD 4.0          FADD
   ┌──────┐          ┌──────┐         ┌──────┐
   │ 2.5  │◄ST(0)    │ 4.0  │◄ST(0)   │ 6.5  │◄ST(0)
   ├──────┤          ├──────┤         ├──────┤
   │      │          │ 2.5  │ ST(1)   │      │
   └──────┘          └──────┘         └──────┘
```

I valori sono tenuti a **80 bit veri** (`Extended80`): 64 bit di mantissa,
contro i 53 di un `double`. La differenza si vede solo in lunghe catene di
calcolo, ma è quella che il 387 aveva.

---

## 7. I dischi: due strade diverse

```
  ┌─────────────────────────────────────────────────────────┐
  │  disco fisso e floppy                                   │
  │                                                          │
  │  programma ──INT 13h──► BIOS ──► DiskImage ──► file .img│
  │                                                          │
  │  Indirizzamento CHS: cilindro / testa / settore          │
  │  Settori da 512 byte                                     │
  └─────────────────────────────────────────────────────────┘

  ┌─────────────────────────────────────────────────────────┐
  │  CD-ROM                                                  │
  │                                                          │
  │  programma ──INT 2Fh──► MSCDEX ──► driver MSCD001        │
  │                                        │                 │
  │                            porte 0x1F0 (ATAPI)           │
  │                                        │                 │
  │                                   IdeController          │
  │                                        │                 │
  │                                   IsoImage ──► file .iso │
  │                                                          │
  │  Settori da 2048 byte, indirizzati per blocco            │
  │  Comandi SCSI dentro pacchetti ATAPI                     │
  └─────────────────────────────────────────────────────────┘
```

Un CD non è un disco più grande: cambia la dimensione del settore, il modo di
indirizzarlo e il protocollo. Fingere che fosse un `DiskImage` sarebbe emerso
al primo `DIR`.

### Il protocollo ATAPI, che è dove si nascondono i difetti

```
   il driver scrive un pacchetto SCSI di 12 byte
        │
        ▼
   il dispositivo alza DRQ e solleva un interrupt   ← primo interrupt
        │
        ▼
   il driver legge i dati dalla porta 0x1F0
        │
        ▼
   il dispositivo abbassa DRQ e solleva un interrupt ← secondo interrupt
```

**Due interrupt per comando**, non uno. Un driver che ne aspetta due e ne
riceve uno resta fermo, senza segnalare nulla.

---

## 8. Il video

La scheda VGA non ha un formato solo: sono almeno tre modi di interpretare gli
stessi indirizzi.

```
  modo testo (0x03)              modo grafico 13h            modo 16 colori
  0xB8000                        0xA0000                     0xA0000
  ┌────┬────┬────┬────┐          ┌──────────────┐            ┌──────────────┐
  │ 'A'│attr│ 'B'│attr│          │ 1 byte =     │            │ 4 piani      │
  └────┴────┴────┴────┘          │ 1 pixel      │            │ sovrapposti  │
   2 byte per carattere          │ 320×200      │            │ 1 bit per    │
   80×25 = 4000 byte             └──────────────┘            │ piano/pixel  │
                                                              └──────────────┘
```

Il modo a 16 colori è il più contorto: quattro **piani di bit** sovrapposti
allo stesso indirizzo, dove ogni pixel prende un bit da ciascuno. Scrivere un
byte tocca otto pixel in un piano solo. Era il modo di avere 16 colori senza
quadruplicare la memoria.

Il generatore di caratteri è **RAM, non ROM**: `MODE CON CP PREPARE` carica un
font diverso, ed è così che il DOS italiano ottiene le lettere accentate.

---

## 9. Il DOS visto dall'emulatore

Aurora386 esegue un **MS-DOS 6.22 vero**, non una sua imitazione. `INT 21h`
lo gestisce il DOS dentro la macchina emulata.

C'è però un punto in cui l'emulatore si inserisce: il **redirector**, la stessa
interfaccia con cui le reti Novell e Microsoft montavano unità remote.

```
   DIR E:
     │
     ▼
   DOS ──► l'unità E: è di un redirector? ──► INT 2Fh, funzione 11h
                                                    │
                                                    ▼
                                          RedirectorServices (C#)
                                                    │
                                    ┌───────────────┴───────────────┐
                                    ▼                               ▼
                          IsoFileSystem                  HostDirectoryFileSystem
                          (il CD, per nome)              (una cartella del Mac)
```

È il meccanismo che permette di vedere una cartella del Mac come un'unità DOS,
senza inventare chiamate che il DOS non conosce.

---

## 10. La struttura del progetto

```
Aurora386
│
├── src/Aurora386.Core        ← il cuore, senza interfaccia
│   ├── Cpu/                  processore (16 file)
│   ├── Bios/                 BIOS, dischi, FPU (16 file)
│   ├── Devices/              hardware emulato (23 file)
│   │   ├── Video/            VGA: registri, memoria, font, disegno
│   │   ├── Audio/            Sound Blaster, OPL3
│   │   └── Network/          NE2000
│   ├── Dos/                  redirector e filesystem
│   ├── Memory/               bus di memoria a pagine
│   └── IO/                   bus delle porte
│
├── src/Aurora386.Gui         interfaccia Avalonia (finestra, menu, mouse)
├── src/Aurora386.Sdl         frontend alternativo SDL2
├── src/Aurora386.FatTool     strumento per iniettare file nelle immagini
└── tests/Aurora386.Tests     754 test
```

**Il nucleo non dipende dall'interfaccia.** È la scelta che rende possibile
scrivere test come questi: un `Machine` si costruisce, si esegue per un numero
preciso di istruzioni, e si esamina — senza aprire una finestra.

```csharp
using var machine = new Machine();
machine.AttachDisk(DiskImage.OpenHardDisk("hdd.img"));
machine.RunInstructions(3_000_000_000);
// e adesso si legge la memoria video per vedere cosa c'è a schermo
```

---

## 11. Il ciclo principale

```
   RunInstructions(n)
        │
        ▼
   ┌──────────────────────────────────────────┐
   │  ripeti a blocchi di 2000 istruzioni:    │
   │                                          │
   │    ┌──────────────────────────────┐      │
   │    │ 2000 × Cpu.StepWithInterrupts│      │
   │    └──────────────┬───────────────┘      │
   │                   ▼                      │
   │    ┌──────────────────────────────┐      │
   │    │ AdvanceDevices(2000)         │      │
   │    │  timer, tastiera, mouse,     │      │
   │    │  disco, audio, rete          │      │
   │    └──────────────┬───────────────┘      │
   │                   ▼                      │
   │        eventi in sospeso, reset          │
   └──────────────────────────────────────────┘
```

I dispositivi non avanzano a ogni istruzione: sarebbe corretto e lentissimo.
Avanzano a blocchi, ricevendo quante istruzioni sono passate. Il blocco è
abbastanza corto da non far perdere interrupt e abbastanza lungo da non pesare.

Un dettaglio che si paga se lo si sbaglia: quando la CPU è in `HLT` il blocco
si interrompe, **ma i dispositivi avanzano comunque**. Solo un interrupt può
risvegliare la CPU, e solo i dispositivi possono produrlo: saltare quel passo
bloccherebbe la macchina per sempre.

---

## 12. Come si verifica

Il progetto ha **754 test**. Alcuni principi che li governano:

**Testare attraverso la macchina, non intorno.** I test dell'FPU assemblano
byte di istruzioni veri e lasciano che la CPU faccia fault; non chiamano
l'emulatore direttamente. La decodifica è metà del lavoro, e un test che la
saltasse passerebbe anche leggendo l'operando dall'indirizzo sbagliato.

**Verificare che il test sappia fallire.** Dopo ogni correzione si
reintroduce il difetto e si controlla che i test diventino rossi. È capitato
più volte di scoprire così che un test non misurava nulla: uno sul log delle
scritture passava anche col codice sabotato, perché l'errore di un cluster
cadeva comunque dentro lo stesso file. È stato riscritto usando due file
adiacenti.

**Preferire un caso che discrimina.** `CONFIG.SYS` e `AUTOEXEC.BAT` occupano
cluster contigui: sbagliare di uno significa nominare il file sbagliato, non
essere "un po' impreciso".

---

## 13. Gli strumenti di diagnostica

Due log, attivabili da variabile d'ambiente perché costano troppo per
tenerli sempre accesi:

```bash
AURORA_WRITELOG=/tmp/scritture.log   # i file che il guest scrive, per nome
AURORA_DOSLOG=/tmp/dos.log           # rename, exec, create, delete
```

Il primo risolve ogni settore scritto al file che lo possiede, leggendo le
tabelle FAT dell'immagine: il tracciato non dice «settore 41328», dice
`C:\WINDOWS\SYSTEM\VGA.DRV`. Il secondo legge i nomi dalla memoria del guest
nel momento della chiamata, perché è l'unico posto dove esistono — a DOS
arriva un puntatore, non un nome.

---

## Nota sulle scelte

Tre decisioni hanno condizionato tutto il resto:

**Interprete invece di JIT.** Un traduttore sarebbe più veloce, ma ogni
difetto diventerebbe difficile da isolare. Per hardware del 1990 la velocità
non è il vincolo.

**BIOS proprio invece di ROM originale.** Costa implementare ogni servizio,
ma rende osservabile ogni passaggio e non dipende da una ROM di cui non si
hanno i diritti.

**Nucleo separato dall'interfaccia.** È ciò che permette di eseguire un'intera
installazione di Windows dentro un test, leggendo lo schermo dalla memoria
video invece che guardandolo.

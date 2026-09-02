#!/bin/zsh
# Esamina un'immagine floppy con DOSBox-X, senza aprire finestre.
#
#   tools/prova-floppy.sh <immagine> [comando DOS...]
#
# Senza comando elenca il contenuto del disco. Con un comando lo esegue
# dentro DOS e ne raccoglie l'output, segnalando se e' terminato.
#
# Esempi:
#   tools/prova-floppy.sh images/msdos622/Disk01.img
#   tools/prova-floppy.sh images/msdos622/Disk01.img mem
#   tools/prova-floppy.sh images/msdos622/Disk01.img type autoexec.bat

set -e

if [[ -z "$1" ]]; then
    echo "uso: $0 <immagine.img> [comando DOS...]" >&2
    exit 1
fi

IMG="$(cd "$(dirname "$1")" && pwd)/$(basename "$1")"
if [[ ! -f "$IMG" ]]; then
    echo "immagine non trovata: $1" >&2
    exit 1
fi

if ! command -v dosbox-x > /dev/null; then
    echo "dosbox-x non installato. Installalo con:  brew install dosbox-x" >&2
    exit 1
fi

shift
COMANDO="${*:-dir}"

WORK=$(mktemp -d)
trap "rm -rf $WORK" EXIT

# Il marcatore finale distingue "il comando e' terminato" da "si e' bloccato":
# e' l'unico modo affidabile di accorgersene senza guardare lo schermo.
cat > "$WORK/dosbox.conf" <<EOF
[sdl]
autolock=false
[dosbox]
machine=svga_s3
memsize=16
[cpu]
core=normal
cputype=386
cycles=fixed 20000
[autoexec]
imgmount a "$IMG" -t floppy
mount c "$WORK"
a:
$COMANDO > C:\\OUT.TXT
echo [TERMINATO] >> C:\\OUT.TXT
exit
EOF

echo "immagine : $(basename "$IMG")"
echo "comando  : $COMANDO"
echo

cd "$WORK"
SDL_VIDEODRIVER=dummy dosbox-x -conf "$WORK/dosbox.conf" > "$WORK/log.txt" 2>&1 &
PID=$!

# Attesa con limite: un comando che non finisce non deve bloccare il terminale.
for i in {1..30}; do
    sleep 1
    kill -0 $PID 2>/dev/null || break
done
kill $PID 2>/dev/null || true
wait $PID 2>/dev/null || true

if [[ -s "$WORK/OUT.TXT" ]]; then
    # Il ritorno a capo di DOS e' CR+LF: si toglie il CR per leggere pulito.
    tr -d '\r' < "$WORK/OUT.TXT"
    echo
    if grep -q "\[TERMINATO\]" "$WORK/OUT.TXT"; then
        echo "--- il comando e' terminato regolarmente ---"
    else
        echo "--- il comando NON e' terminato (bloccato o in attesa di input) ---"
    fi
else
    echo "(nessun output)"
    echo "--- il comando non ha prodotto nulla ---"
fi

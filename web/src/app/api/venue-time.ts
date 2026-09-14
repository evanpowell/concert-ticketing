/**
 * A show time belongs to the venue, not the viewer. Doors at 8pm are at 8pm
 * whether you are buying from San Francisco or Tokyo, so times are rendered in
 * the venue's IANA zone rather than the browser's.
 *
 * The zone must be IANA rather than a fixed UTC offset: The Chapel is -08:00 in
 * November and -07:00 in June, so an offset stored alongside the event would be
 * wrong for half the year. The instant in `startsAt` is already unambiguous;
 * this is purely presentation.
 */
const formatters = new Map<string, Intl.DateTimeFormat>();

function formatterFor(timeZone: string): Intl.DateTimeFormat {
  let formatter = formatters.get(timeZone);
  if (!formatter) {
    formatter = new Intl.DateTimeFormat('en-US', {
      weekday: 'short',
      month: 'short',
      day: 'numeric',
      hour: 'numeric',
      minute: '2-digit',
      timeZone,
      // Without this the reader cannot tell whose clock they are looking at.
      timeZoneName: 'short',
    });
    formatters.set(timeZone, formatter);
  }
  return formatter;
}

export function formatInVenueTime(isoInstant: string, timeZone: string): string {
  try {
    return formatterFor(timeZone).format(new Date(isoInstant));
  } catch {
    // An unknown zone would throw a RangeError and blank the whole listing;
    // falling back to UTC keeps the page usable and is visibly labelled.
    return formatterFor('UTC').format(new Date(isoInstant));
  }
}

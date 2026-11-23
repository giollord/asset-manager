// Blob URLs for originals shown in the app (full-resolution images, full-length audio and video): the bytes come from
// .NET as a stream, so no base64 data URL is built for files of tens of megabytes.
export async function blobUrl(streamRef, mime) {
  const buffer = await streamRef.arrayBuffer();
  return URL.createObjectURL(new Blob([buffer], { type: mime }));
}

export function revoke(url) {
  if (url) URL.revokeObjectURL(url);
}

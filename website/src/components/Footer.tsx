export default function Footer() {
  return (
    <footer className="border-t border-neutral-900 min-w-0">
      <div className="mx-auto max-w-5xl px-4 sm:px-6 py-8 flex flex-col sm:flex-row items-start sm:items-center justify-between gap-3 text-sm text-neutral-400">
        <div>
          © {new Date().getFullYear()} OpenServiceBus · Released under the MIT license
        </div>
        <div className="flex items-center gap-4">
          <a className="hover:text-neutral-300 transition" href="https://github.com/mauritsarissen/OpenServiceBus" target="_blank" rel="noreferrer">GitHub</a>
          <a className="hover:text-neutral-300 transition" href="https://www.nuget.org/packages?q=OpenServiceBus" target="_blank" rel="noreferrer">NuGet</a>
          <a className="hover:text-neutral-300 transition" href="https://hub.docker.com/r/mauritsarissen/openservicebus" target="_blank" rel="noreferrer">Docker Hub</a>
        </div>
      </div>
    </footer>
  );
}

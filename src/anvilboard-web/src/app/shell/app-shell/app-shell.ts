import { Component, inject, signal } from '@angular/core';
import { RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';
import { BoardApiService } from '../../core/board-api.service';
import { Team } from '../../core/models';

@Component({
  imports: [RouterLink, RouterLinkActive, RouterOutlet],
  selector: 'app-app-shell',
  styleUrl: './app-shell.scss',
  templateUrl: './app-shell.html',
})
export class AppShell {
  private readonly api = inject(BoardApiService);

  readonly teams = signal<Team[]>([]);

  constructor() {
    this.api.listTeams().subscribe((teams) => this.teams.set(teams));
  }
}

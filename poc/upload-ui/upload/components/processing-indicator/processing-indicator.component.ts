import { ChangeDetectionStrategy, Component, Input } from '@angular/core';

@Component({
  selector: 'app-processing-indicator',
  templateUrl: './processing-indicator.component.html',
  styleUrls: ['./processing-indicator.component.scss'],
  changeDetection: ChangeDetectionStrategy.OnPush
})
export class ProcessingIndicatorComponent {
  @Input() label = 'Working…';
}

using System;
using System.Threading.Tasks;

namespace NINA.Plugin.Livestack.Utility {

    public class TaskManager {
        private Task _currentTask;
        private object _lock = new object();

        public Task ExecuteOnceAsync(Func<Task> taskFactory) {
            if (_currentTask == null) {
                lock (_lock) {
                    if (_currentTask == null) {
                        Task concreteTask = taskFactory();
                        concreteTask.ContinueWith(o => { RemoveTask(); });
                        _currentTask = concreteTask;
                    }
                }
            }

            return _currentTask;
        }

        private void RemoveTask() {
            _currentTask = null;
        }
    }
}
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class UVAlphaTest : MonoBehaviour
{
    [SerializeField] GameObject emitter;
    [SerializeField] float offset;
    [SerializeField] int xRepeat, yRepeat;
    // Start is called before the first frame update
    void Start()
    {
        for(int x = 0; x < xRepeat; x++){
            for(int y = 0; y < yRepeat; y++){
                GameObject newEmitter = Instantiate(emitter, transform);
                newEmitter.transform.localPosition = new Vector3(x * offset, 0, y * offset);
            }
        }
    }

    // Update is called once per frame
    void Update()
    {
        
    }
}
